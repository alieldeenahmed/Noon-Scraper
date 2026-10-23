using Microsoft.EntityFrameworkCore;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.DemoData;

public sealed record SeedResult(int Products, int Snapshots, int RestockEvents, int DiscountFlags);

// SYNTHETIC DEMO DATA. Nothing here was scraped from noon.com.
//
// Loads a few dozen invented products with ~70 days of daily price history so the
// UI is meaningful without running the scraper. Every product is labelled: the name
// starts with "[Demo]", the merchant is "Demo Store (synthetic)", and the product
// code starts with "DEMO". The URLs have the shape of a noon.com product link but
// point at nothing; the products are Source = Seed, so no crawl will ever visit them.
//
// The restock events and discount flags are not written by hand. The seeder replays
// each product's history through the same PriceHistoryAnalyzer the crawler uses, in
// the same order ProductRecorder does (rules run against saved history, then the new
// snapshot and its event/flag are saved together), so what the UI shows is what the
// rules would really have produced for these prices.
public static class DemoSeeder
{
    public const string SkuPrefix = "DEMO";
    public const string MerchantName = "Demo Store (synthetic)";
    public const string NamePrefix = "[Demo] ";
    public const int HistoryDays = 70;

    private enum Pattern
    {
        // Price wobbles around a base; no sale.
        Steady,
        // Price really drops below anything it has recently been, with a discount badge.
        GenuineSale,
        // Price spikes ~40% for a week, then "drops" 25% to a price still above its old
        // low. The advertised discount is real against the spike, but not against the
        // product's own history - the case the fake-discount rule exists for.
        FakeDiscount,
        // The same trick twice, a month apart.
        FakeDiscountTwice,
        // A discount badge on a price that has been flat all along: no spike, so the
        // rule must NOT flag it.
        BadgeOnFlatPrice,
        // Out of stock for a stretch, then back.
        Restock,
        // Out of stock as of the latest crawl.
        OutOfStockNow,
        // A random walk of a few percent a day.
        Volatile
    }

    private sealed record Spec(string Name, Category Category, int BasePrice, Pattern Pattern);

    // 36 invented products: six in each of the five tracked categories, plus Electronics and Other.
    private static readonly Spec[] Specs =
    [
        new("Smartphone Aurora 128GB", Category.Mobiles, 14500, Pattern.FakeDiscount),
        new("Smartphone Nimbus 256GB", Category.Mobiles, 21900, Pattern.GenuineSale),
        new("Smartphone Pebble Lite 64GB", Category.Mobiles, 5200, Pattern.Steady),
        new("Smartphone Orbit 5G 128GB", Category.Mobiles, 11800, Pattern.Volatile),
        new("Smartphone Ember 512GB", Category.Mobiles, 33500, Pattern.FakeDiscountTwice),
        new("Smartphone Drift Mini", Category.Mobiles, 8900, Pattern.Restock),

        new("Laptop Slate 14\" i5 16GB", Category.Laptops, 24900, Pattern.Steady),
        new("Laptop Forge 15\" Ryzen 7", Category.Laptops, 38500, Pattern.FakeDiscount),
        new("Laptop Breeze Air 13\"", Category.Laptops, 29900, Pattern.GenuineSale),
        new("Laptop Titan Gaming 17\"", Category.Laptops, 61000, Pattern.Volatile),
        new("Laptop Campus 11\" Student", Category.Laptops, 9800, Pattern.BadgeOnFlatPrice),
        new("Laptop Studio 16\" Creator", Category.Laptops, 72000, Pattern.OutOfStockNow),

        new("Hydrating Face Cleanser 400ml", Category.SkinCare, 420, Pattern.Steady),
        new("Vitamin C Serum 30ml", Category.SkinCare, 780, Pattern.FakeDiscount),
        new("Daily SPF 50 Sunscreen 50ml", Category.SkinCare, 560, Pattern.GenuineSale),
        new("Night Repair Cream 50ml", Category.SkinCare, 910, Pattern.FakeDiscountTwice),
        new("Gentle Micellar Water 250ml", Category.SkinCare, 330, Pattern.Restock),
        new("Clay Mask Detox 100ml", Category.SkinCare, 475, Pattern.BadgeOnFlatPrice),

        new("Repair Shampoo 400ml", Category.HairCare, 390, Pattern.Steady),
        new("Argan Hair Oil 100ml", Category.HairCare, 640, Pattern.FakeDiscount),
        new("Curl Defining Cream 200ml", Category.HairCare, 520, Pattern.Volatile),
        new("Anti-Dandruff Conditioner 300ml", Category.HairCare, 360, Pattern.GenuineSale),
        new("Heat Protect Spray 150ml", Category.HairCare, 445, Pattern.Restock),
        new("Keratin Mask 250ml", Category.HairCare, 830, Pattern.OutOfStockNow),

        new("Men's Eau de Parfum 60ml", Category.PersonalCare, 1650, Pattern.FakeDiscountTwice),
        new("Roll-On Deodorant 50ml", Category.PersonalCare, 135, Pattern.Steady),
        new("Body Spray Fresh 150ml", Category.PersonalCare, 210, Pattern.BadgeOnFlatPrice),
        new("Electric Shaver Pro", Category.PersonalCare, 2450, Pattern.GenuineSale),
        new("Beard Care Kit", Category.PersonalCare, 690, Pattern.FakeDiscount),
        new("Whitening Toothpaste 120ml", Category.PersonalCare, 95, Pattern.Volatile),

        new("Wireless Earbuds Echo", Category.Electronics, 2300, Pattern.FakeDiscount),
        new("Over-Ear Headphones Studio", Category.Electronics, 4800, Pattern.GenuineSale),
        new("Smart Watch Pulse", Category.Electronics, 3900, Pattern.Restock),

        new("Insulated Travel Mug 40oz", Category.Other, 480, Pattern.Steady),
        new("Non-Stick Frying Pan 28cm", Category.Other, 760, Pattern.FakeDiscountTwice),
        new("Desk Organizer Set", Category.Other, 310, Pattern.BadgeOnFlatPrice)
    ];

    public static async Task<SeedResult> RunAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct = default)
    {
        // The run makes thousands of small statements. Holding one connection open for
        // all of them keeps it fast whether or not the connection string enables
        // pooling (without it, every statement would open a fresh connection) and over
        // a remote database's round-trip time.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            return await SeedAsync(db, now, ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<SeedResult> SeedAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Never mix invented rows into a database that holds real ones.
        var real = await db.Products.AnyAsync(
            p => p.NoonProductId == null || !p.NoonProductId.StartsWith(SkuPrefix), ct);
        if (real)
        {
            throw new InvalidOperationException(
                "Refusing to seed: this database contains products that are not demo data. " +
                "The seeder only runs against an empty database (or one it seeded itself).");
        }

        // Re-running replaces the previous demo set (children cascade with the product).
        await db.Products.Where(p => p.NoonProductId!.StartsWith(SkuPrefix)).ExecuteDeleteAsync(ct);

        var rng = new Random(20260101);
        int snapshots = 0, restocks = 0, flags = 0;

        for (var i = 0; i < Specs.Length; i++)
        {
            var spec = Specs[i];
            var series = BuildSeries(spec, rng);

            var product = new Product
            {
                Url = $"https://www.noon.com/egypt-en/synthetic-demo-{i + 1}/{SkuPrefix}{i + 1:D4}/p/",
                NoonProductId = $"{SkuPrefix}{i + 1:D4}",
                Name = NamePrefix + spec.Name,
                MerchantName = MerchantName,
                Category = spec.Category,
                Rating = Math.Round((decimal)(3.4 + rng.NextDouble() * 1.5), 1),
                Source = ProductSource.Seed,
                IsActive = true,
                AddedAt = now.AddDays(-HistoryDays)
            };
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);

            for (var day = 0; day < series.Length; day++)
            {
                var point = series[day];
                var at = now.AddDays(-(HistoryDays - 1 - day)).AddMinutes(-(day == series.Length - 1 ? 5 : rng.Next(5, 90)));

                // Same order as ProductRecorder: rules run against saved history, then the
                // new snapshot and whatever it triggered are saved together.
                var restocked = await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, point.InStock, ct);

                var snapshot = new PriceSnapshot
                {
                    ProductId = product.Id,
                    Product = product,
                    Price = point.Price,
                    Stock = point.InStock,
                    DiscountPercent = point.DiscountPercent,
                    CrawledAt = at
                };
                db.PriceSnapshots.Add(snapshot);

                if (restocked)
                {
                    db.RestockEvents.Add(new RestockEvent
                    {
                        ProductId = product.Id,
                        Product = product,
                        TriggeringSnapshotId = snapshot.Id,
                        TriggeringSnapshot = snapshot,
                        DetectedAt = at
                    });
                    restocks++;
                }

                var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(
                    db, product, snapshot, point.Price, point.DiscountPercent, at, ct);
                if (flag is not null)
                {
                    db.DiscountFlags.Add(flag);
                    flags++;
                }

                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                db.Attach(product);
                snapshots++;
            }

            db.ChangeTracker.Clear();
        }

        return new SeedResult(Specs.Length, snapshots, restocks, flags);
    }

    private readonly record struct Point(decimal Price, bool InStock, decimal? DiscountPercent);

    // One entry per day, oldest first.
    private static Point[] BuildSeries(Spec spec, Random rng)
    {
        var days = HistoryDays;
        var basePrice = (double)spec.BasePrice;
        var points = new Point[days];

        // Day-to-day wobble of +/-1.5%, so "steady" doesn't mean identical numbers.
        decimal Wobble(double level) => Math.Round((decimal)(level * (1 + (rng.NextDouble() - 0.5) * 0.03)));

        double walk = basePrice;
        for (var d = 0; d < days; d++)
        {
            var daysAgo = days - 1 - d;
            decimal price = Wobble(basePrice);
            var inStock = true;
            decimal? badge = null;

            switch (spec.Pattern)
            {
                case Pattern.Steady:
                    break;

                case Pattern.Volatile:
                    walk = Math.Clamp(walk * (1 + (rng.NextDouble() - 0.5) * 0.10), basePrice * 0.8, basePrice * 1.25);
                    price = Math.Round((decimal)walk);
                    break;

                case Pattern.GenuineSale:
                    if (daysAgo < 6)
                    {
                        price = Wobble(basePrice * 0.75);
                        badge = 25;
                    }
                    break;

                case Pattern.FakeDiscount:
                    ApplyFake(daysAgo, spikeFrom: 13, saleFrom: 6);
                    break;

                case Pattern.FakeDiscountTwice:
                    ApplyFake(daysAgo, spikeFrom: 44, saleFrom: 38);
                    ApplyFake(daysAgo, spikeFrom: 13, saleFrom: 6);
                    break;

                case Pattern.BadgeOnFlatPrice:
                    // Advertised as 15% off, but the price has been this all along.
                    if (daysAgo < 10)
                    {
                        badge = 15;
                    }
                    break;

                case Pattern.Restock:
                    inStock = !(daysAgo is <= 24 and >= 14);
                    break;

                case Pattern.OutOfStockNow:
                    inStock = daysAgo > 3;
                    break;
            }

            points[d] = new Point(price, inStock, badge);

            void ApplyFake(int ago, int spikeFrom, int saleFrom)
            {
                if (ago <= spikeFrom && ago > saleFrom)
                {
                    // The spike week: price quietly goes up ~40%, no badge.
                    price = Wobble(basePrice * 1.4);
                }
                else if (ago <= saleFrom && ago > saleFrom - 4)
                {
                    // The "sale": 25% off the spiked price, which is still above the old low.
                    price = Wobble(basePrice * 1.05);
                    badge = 25;
                }
            }
        }

        return points;
    }
}
