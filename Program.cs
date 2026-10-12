using BlazorApp1.Components;
using BlazorApp1.Services;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddOpenApi();
builder.Services.Configure<ZarinPalOptions>(builder.Configuration.GetSection(ZarinPalOptions.SectionName));
builder.Services.AddHttpClient<OrderPaymentService>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration.GetValue<bool>("ZarinPal:Sandbox")
        ? "https://sandbox.zarinpal.com/pg/v4/"
        : "https://api.zarinpal.com/pg/v4/");
    client.Timeout = TimeSpan.FromSeconds(20);
});

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "orders.db");
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared,
    DefaultTimeout = 15
}.ToString();
builder.Services.AddSingleton<IOrderStore>(new SqliteOrderStore(connectionString));

var app = builder.Build();
await app.Services.GetRequiredService<IOrderStore>().InitializeAsync(CancellationToken.None);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/payment/zarinpal/callback", async (
        HttpContext context,
        OrderPaymentService paymentService,
        CancellationToken cancellationToken) =>
    {
        var status = context.Request.Query["Status"].FirstOrDefault();
        var authority = context.Request.Query["Authority"].FirstOrDefault();
        var redirectUrl = await paymentService.CompleteCallbackAsync(status, authority, cancellationToken);
        return TypedResults.Redirect(redirectUrl);
    })
    .WithName("ZarinPalPaymentCallback")
    .WithSummary("Receives and verifies the ZarinPal payment result")
    .WithDescription("Verifies the payment with ZarinPal before marking the order as paid.")
    .Produces(StatusCodes.Status302Found);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
