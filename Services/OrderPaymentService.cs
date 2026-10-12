using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BlazorApp1.Services;

public sealed class ZarinPalOptions
{
    public const string SectionName = "ZarinPal";

    public string MerchantId { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public bool Sandbox { get; set; } = true;
}

public sealed record CheckoutItem(int ProductId, int Quantity);

public sealed record CheckoutRequest(
    string CustomerName,
    string Mobile,
    string Address,
    IReadOnlyList<CheckoutItem> Items);

public sealed record PaymentStartResult(bool Succeeded, string? Error, Uri? RedirectUrl, Guid? OrderId);

public sealed record PaymentReceipt(string Status, string? ReferenceId);

public sealed class OrderPaymentService(
    HttpClient httpClient,
    IOrderStore orderStore,
    IOptions<ZarinPalOptions> options,
    ILogger<OrderPaymentService> logger)
{
    private static readonly Regex IranianMobilePattern = new("^09\\d{9}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PaymentStartResult> StartAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetConfiguration(out var merchantId, out var callbackUrl, out var error))
        {
            return new PaymentStartResult(false, error, null, null);
        }

        request = request with { Mobile = NormalizeDigits(request.Mobile.Trim()) };
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return new PaymentStartResult(false, validationError, null, null);
        }

        var products = CoffeeCatalog.Products.ToDictionary(product => product.Id);
        long amountToman;
        try
        {
            amountToman = request.Items.Aggregate(
                0L,
                (total, item) => checked(total + checked((long)products[item.ProductId].Price * item.Quantity)));
        }
        catch (OverflowException)
        {
            return new PaymentStartResult(false, "مبلغ سفارش بیش از حد مجاز است.", null, null);
        }

        var deliveryFeeToman = OrderPricing.GetDeliveryFee(amountToman);
        var totalToman = checked(amountToman + deliveryFeeToman);
        var orderId = Guid.NewGuid();
        var order = new OrderRecord(
            orderId,
            request.CustomerName.Trim(),
            request.Mobile.Trim(),
            request.Address.Trim(),
            JsonSerializer.Serialize(request.Items, JsonOptions),
            amountToman,
            deliveryFeeToman,
            totalToman,
            checked(totalToman * 10),
            DateTimeOffset.UtcNow,
            "Pending",
            null,
            null);

        try
        {
            await orderStore.SaveAsync(order, cancellationToken);
        }
        catch (SqliteException exception)
        {
            logger.LogError(exception, "Could not save order {OrderId}.", orderId);
            return new PaymentStartResult(false, "ثبت سفارش با خطا روبه‌رو شد. لطفاً کمی بعد دوباره تلاش کن.", null, null);
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "payment/request.json",
                new
                {
                    merchant_id = merchantId,
                    amount = order.AmountRial,
                    callback_url = callbackUrl,
                    description = $"سفارش قهوه {order.Id:N}",
                    metadata = new { mobile = order.Mobile }
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ZarinPal payment request returned HTTP {StatusCode} for order {OrderId}.", (int)response.StatusCode, orderId);
                await orderStore.SetPaymentStatusAsync(orderId, "PaymentInitiationFailed", null, null, cancellationToken);
                return new PaymentStartResult(false, "درگاه پرداخت در دسترس نیست. لطفاً کمی بعد دوباره تلاش کن.", null, orderId);
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            if (!TryGetCode(document.RootElement, out var code)
                || code != 100
                || !TryGetValueText(document.RootElement, "data", "authority", out var authority)
                || string.IsNullOrWhiteSpace(authority))
            {
                logger.LogWarning("ZarinPal rejected the payment request for order {OrderId} with code {Code}.", orderId, code);
                await orderStore.SetPaymentStatusAsync(orderId, "PaymentInitiationFailed", null, null, cancellationToken);
                return new PaymentStartResult(false, "ساخت لینک پرداخت انجام نشد. لطفاً اطلاعات درگاه را بررسی کن.", null, orderId);
            }

            await orderStore.SetPaymentStatusAsync(orderId, "AwaitingPayment", authority, null, cancellationToken);
            var paymentHost = options.Value.Sandbox ? "sandbox.zarinpal.com" : "www.zarinpal.com";
            return new PaymentStartResult(
                true,
                null,
                new Uri($"https://{paymentHost}/pg/StartPay/{Uri.EscapeDataString(authority)}"),
                orderId);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Could not connect to ZarinPal while creating payment for order {OrderId}.", orderId);
            await orderStore.SetPaymentStatusAsync(orderId, "PaymentInitiationFailed", null, null, cancellationToken);
            return new PaymentStartResult(false, "ارتباط با درگاه پرداخت برقرار نشد. لطفاً دوباره تلاش کن.", null, orderId);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "ZarinPal returned an invalid payment request response for order {OrderId}.", orderId);
            await orderStore.SetPaymentStatusAsync(orderId, "PaymentInitiationFailed", null, null, cancellationToken);
            return new PaymentStartResult(false, "پاسخ درگاه پرداخت معتبر نبود. لطفاً دوباره تلاش کن.", null, orderId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("ZarinPal payment request timed out for order {OrderId}.", orderId);
            await orderStore.SetPaymentStatusAsync(orderId, "PaymentInitiationFailed", null, null, CancellationToken.None);
            return new PaymentStartResult(false, "درخواست پرداخت بیش از حد طول کشید. لطفاً دوباره تلاش کن.", null, orderId);
        }
    }

    public async Task<string> CompleteCallbackAsync(
        string? status,
        string? authority,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return "/?payment=unknown";
        }

        OrderRecord? order;
        try
        {
            order = await orderStore.FindByAuthorityAsync(authority, cancellationToken);
        }
        catch (SqliteException exception)
        {
            logger.LogError(exception, "Could not load payment callback order.");
            return "/?payment=error";
        }

        if (order is null)
        {
            logger.LogWarning("Received a ZarinPal callback for an unknown authority.");
            return "/?payment=unknown";
        }

        if (order.PaymentStatus == "Paid")
        {
            return BuildOrderRedirect("complete", order.Id);
        }

        if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            return BuildOrderRedirect("cancelled", order.Id);
        }

        if (order.PaymentStatus != "AwaitingPayment")
        {
            return BuildOrderRedirect("failed", order.Id);
        }

        if (!TryGetConfiguration(out var merchantId, out _, out _))
        {
            logger.LogError("Cannot verify ZarinPal payment because gateway configuration is missing.");
            return BuildOrderRedirect("error", order.Id);
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "payment/verify.json",
                new
                {
                    merchant_id = merchantId,
                    amount = order.AmountRial,
                    authority
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ZarinPal payment verification returned HTTP {StatusCode} for order {OrderId}.", (int)response.StatusCode, order.Id);
                return BuildOrderRedirect("error", order.Id);
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            if (!TryGetCode(document.RootElement, out var code)
                || code is not (100 or 101)
                || !TryGetValueText(document.RootElement, "data", "ref_id", out var referenceId)
                || string.IsNullOrWhiteSpace(referenceId))
            {
                logger.LogWarning("ZarinPal did not verify order {OrderId}; response code {Code}.", order.Id, code);
                await orderStore.SetPaymentStatusAsync(order.Id, "VerificationFailed", null, null, cancellationToken);
                return BuildOrderRedirect("failed", order.Id);
            }

            await orderStore.SetPaymentStatusAsync(order.Id, "Paid", null, referenceId, cancellationToken);
            return BuildOrderRedirect("complete", order.Id);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Could not verify payment for order {OrderId}.", order.Id);
            return BuildOrderRedirect("error", order.Id);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "ZarinPal returned an invalid verification response for order {OrderId}.", order.Id);
            return BuildOrderRedirect("error", order.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("ZarinPal verification timed out for order {OrderId}.", order.Id);
            return BuildOrderRedirect("error", order.Id);
        }
        catch (SqliteException exception)
        {
            logger.LogError(exception, "Could not update verification result for order {OrderId}.", order.Id);
            return BuildOrderRedirect("error", order.Id);
        }
    }

    public async Task<PaymentReceipt?> GetReceiptAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            var order = await orderStore.FindByIdAsync(orderId, cancellationToken);
            return order is null ? null : new PaymentReceipt(order.PaymentStatus, order.ReferenceId);
        }
        catch (SqliteException exception)
        {
            logger.LogError(exception, "Could not load payment receipt for order {OrderId}.", orderId);
            return null;
        }
    }

    private bool TryGetConfiguration(out string merchantId, out Uri callbackUrl, out string? error)
    {
        merchantId = options.Value.MerchantId.Trim();
        callbackUrl = new Uri("https://localhost/");
        error = null;

        if (!Guid.TryParse(merchantId, out _))
        {
            error = "درگاه زرین‌پال هنوز تنظیم نشده؛ مرچنت‌کد را در تنظیمات برنامه وارد کن.";
            return false;
        }

        if (!Uri.TryCreate(options.Value.PublicBaseUrl.Trim(), UriKind.Absolute, out var publicUri)
            || (publicUri.Scheme != Uri.UriSchemeHttps
                && !(options.Value.Sandbox
                    && publicUri.Scheme == Uri.UriSchemeHttp
                    && (publicUri.IsLoopback || publicUri.Host.EndsWith(".ngrok-free.app", StringComparison.OrdinalIgnoreCase)))))
        {
            error = "نشانی عمومی سایت باید HTTPS باشد و در تنظیمات زرین‌پال ثبت شود.";
            return false;
        }

        callbackUrl = new Uri(publicUri, "/payment/zarinpal/callback");
        return true;
    }

    private static string? Validate(CheckoutRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerName) || request.CustomerName.Trim().Length is < 2 or > 100)
        {
            return "نام و نام خانوادگی را وارد کن.";
        }

        if (!IranianMobilePattern.IsMatch(request.Mobile.Trim()))
        {
            return "شماره‌ی موبایل باید با ۰۹ شروع شود و ۱۱ رقم باشد.";
        }

        if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Trim().Length is < 10 or > 500)
        {
            return "نشانی کامل محل تحویل را وارد کن.";
        }

        if (request.Items.Count is < 1 or > 20
            || request.Items.Any(item => item.Quantity is < 1 or > 20)
            || request.Items.Select(item => item.ProductId).Distinct().Count() != request.Items.Count)
        {
            return "سبد خرید معتبر نیست؛ لطفاً دوباره بررسی‌اش کن.";
        }

        var productIds = CoffeeCatalog.Products.Select(product => product.Id).ToHashSet();
        return request.Items.Any(item => !productIds.Contains(item.ProductId))
            ? "یکی از کالاهای انتخاب‌شده در منوی فعلی موجود نیست."
            : null;
    }

    private static string BuildOrderRedirect(string paymentState, Guid orderId) =>
        $"/?payment={Uri.EscapeDataString(paymentState)}&orderId={Uri.EscapeDataString(orderId.ToString("D"))}";

    private static bool TryGetCode(JsonElement root, out int code)
    {
        code = 0;
        return root.TryGetProperty("data", out var data)
            && data.TryGetProperty("code", out var codeElement)
            && codeElement.TryGetInt32(out code);
    }

    private static bool TryGetValueText(JsonElement root, string parentName, string propertyName, out string value)
    {
        value = "";
        if (!root.TryGetProperty(parentName, out var parent)
            || !parent.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return value.Length > 0;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetRawText();
            return value.Length > 0;
        }

        return false;
    }

    public static string NormalizeDigits(string value) =>
        new(value.Select(character => character switch
        {
            >= '۰' and <= '۹' => (char)('0' + character - '۰'),
            >= '٠' and <= '٩' => (char)('0' + character - '٠'),
            _ => character
        }).ToArray());
}
