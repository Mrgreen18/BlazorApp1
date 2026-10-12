namespace BlazorApp1.Services;

public sealed record CoffeeProduct(
    int Id,
    string Name,
    string Description,
    string Origin,
    string Category,
    int Price,
    string Weight,
    string Badge,
    string Image,
    string Rating);

public static class CoffeeCatalog
{
    public static IReadOnlyList<CoffeeProduct> Products { get; } =
    [
        new(1, "بلِند صبح دل‌انگیز", "ترکیبی، متعادل و شیرین", "برزیل · کلمبیا", "اسپرسو", 285000, "۲۵۰ گرم", "محبوب", "https://images.unsplash.com/photo-1559525839-b184a4d698c7?auto=format&fit=crop&w=700&q=80", "۴.۹"),
        new(2, "اتیوپی یرگاچف", "نت گلی، مرکباتی و لطیف", "اتیوپی · شسته", "دَمی", 345000, "۲۵۰ گرم", "تک‌خاستگاه", "https://images.unsplash.com/photo-1552346989-e069318e20a5?auto=format&fit=crop&w=700&q=80", "۵.۰"),
        new(3, "کلمبیا هویلا", "شکلاتی، آجیلی و کاراملی", "کلمبیا · طبیعی", "اسپرسو", 315000, "۲۵۰ گرم", "", "https://images.unsplash.com/photo-1611854779393-1b2da9d400fe?auto=format&fit=crop&w=700&q=80", "۴.۸"),
        new(4, "کولد برو تابستونی", "خنک، میوه‌ای و پرانرژی", "دم‌آوری سرد", "سرد", 195000, "۳۰۰ میلی‌لیتر", "آماده نوشیدن", "https://images.unsplash.com/photo-1461023058943-07fcbe16d735?auto=format&fit=crop&w=700&q=80", "۴.۹")
    ];
}

public static class OrderPricing
{
    public const long FreeDeliveryThresholdToman = 700_000;
    public const long DeliveryFeeToman = 50_000;

    public static long GetDeliveryFee(long subtotalToman) =>
        subtotalToman >= FreeDeliveryThresholdToman ? 0 : DeliveryFeeToman;
}
