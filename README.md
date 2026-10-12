# BlazorApp1

A Persian RTL coffee shop storefront built with Blazor Server and ASP.NET Core.

## Run locally

```bash
dotnet run --project BlazorApp1.csproj
```

Orders are stored in `App_Data/orders.db`, which is intentionally excluded from Git.

## ZarinPal checkout

The checkout defaults to sandbox mode. Set the merchant ID and the public HTTPS
base URL before starting the app:

```bash
export ZarinPal__MerchantId="<merchant UUID>"
export ZarinPal__PublicBaseUrl="https://your-public-domain"
export ZarinPal__Sandbox="true"
```

The configured callback is `/payment/zarinpal/callback`; it must be reachable
from the internet. Set `ZarinPal__Sandbox` to `false` only when using the
production merchant account and production HTTPS domain.

Delivery costs 50,000 toman for orders below 700,000 toman and is free above
that threshold. The server recalculates item prices and delivery before
requesting payment.
