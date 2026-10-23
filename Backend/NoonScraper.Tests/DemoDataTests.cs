using Microsoft.EntityFrameworkCore;
using NoonScraper.Data;
using NoonScraper.Data.Models;
using NoonScraper.DemoData;

namespace NoonScraper.Tests;

// The synthetic demo data has to be honestly labelled, has to come from the real
// rules (not hand-written flags), and must never be loaded on top of real data.
public class DemoDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Loads_a_few_dozen_products_with_about_seventy_days_of_history()
    {
        using var db = TestDb.Create();

        var result = await DemoSeeder.RunAsync(db, Now);

        Assert.Equal(36, result.Products);
        Assert.Equal(36, db.Products.Count());
        Assert.Equal(36 * DemoSeeder.HistoryDays, db.PriceSnapshots.Count());
        Assert.All(db.Products.Select(p => p.PriceSnapshots.Count).ToList(), n => Assert.Equal(DemoSeeder.HistoryDays, n));
    }

    [Fact]
    public async Task Every_product_is_labelled_synthetic_and_never_crawlable()
    {
        using var db = TestDb.Create();

        await DemoSeeder.RunAsync(db, Now);

        var products = db.Products.AsNoTracking().ToList();
        Assert.All(products, p =>
        {
            Assert.StartsWith(DemoSeeder.NamePrefix, p.Name);
            Assert.Equal(DemoSeeder.MerchantName, p.MerchantName);
            Assert.StartsWith(DemoSeeder.SkuPrefix, p.NoonProductId);
            // Seed-source products are only ever re-crawled by finding them on a
            // category page, which these never appear on.
            Assert.Equal(ProductSource.Seed, p.Source);
        });
    }

    [Fact]
    public async Task Covers_every_category()
    {
        using var db = TestDb.Create();

        await DemoSeeder.RunAsync(db, Now);

        var categories = db.Products.Select(p => p.Category).Distinct().ToList();
        foreach (var category in Enum.GetValues<Category>())
        {
            Assert.Contains(category, categories);
        }
    }

    [Fact]
    public async Task Flags_and_restocks_come_from_the_real_rules()
    {
        using var db = TestDb.Create();

        var result = await DemoSeeder.RunAsync(db, Now);

        Assert.True(result.DiscountFlags > 0, "the demo set should contain fake discounts");
        Assert.True(result.RestockEvents > 0, "the demo set should contain restocks");
        Assert.Equal(result.DiscountFlags, db.DiscountFlags.Count());
        Assert.Equal(result.RestockEvents, db.RestockEvents.Count());

        // Each flag satisfies the rule's definition, checked against its own recorded numbers:
        // the price before was at least 10% above the low, and the "discounted" price did
        // not beat that low.
        var flags = db.DiscountFlags.AsNoTracking().ToList();
        Assert.All(flags, f =>
        {
            Assert.True(f.PriorHighPrice >= f.HistoricalLowPrice * PriceHistoryAnalyzer.SpikeThreshold);
            Assert.True(f.DiscountedPrice >= f.HistoricalLowPrice);
        });
    }

    [Fact]
    public async Task Genuine_sales_and_flat_price_badges_are_not_flagged()
    {
        using var db = TestDb.Create();

        await DemoSeeder.RunAsync(db, Now);

        // "Smartphone Nimbus 256GB" has a real 25% price drop; "Laptop Campus 11\" Student"
        // carries a 15% badge on a price that never moved. Neither is a fake discount.
        Assert.Equal(0, db.DiscountFlags.Count(f => f.Product!.Name!.Contains("Nimbus")));
        Assert.Equal(0, db.DiscountFlags.Count(f => f.Product!.Name!.Contains("Campus")));
        // ...while the product built to be fake is flagged.
        Assert.True(db.DiscountFlags.Count(f => f.Product!.Name!.Contains("Aurora")) >= 1);
    }

    [Fact]
    public async Task Running_it_again_replaces_the_demo_set_instead_of_duplicating_it()
    {
        using var db = TestDb.Create();
        await DemoSeeder.RunAsync(db, Now);

        await DemoSeeder.RunAsync(db, Now);

        Assert.Equal(36, db.Products.Count());
        Assert.Equal(36 * DemoSeeder.HistoryDays, db.PriceSnapshots.Count());
    }

    [Fact]
    public async Task The_same_clock_gives_the_same_data()
    {
        using var first = TestDb.Create();
        using var second = TestDb.Create();

        var a = await DemoSeeder.RunAsync(first, Now);
        var b = await DemoSeeder.RunAsync(second, Now);

        Assert.Equal(a, b);
        Assert.Equal(
            first.PriceSnapshots.OrderBy(s => s.ProductId).ThenBy(s => s.CrawledAt).Select(s => s.Price).ToList(),
            second.PriceSnapshots.OrderBy(s => s.ProductId).ThenBy(s => s.CrawledAt).Select(s => s.Price).ToList());
    }

    [Fact]
    public async Task Refuses_to_run_on_a_database_that_holds_real_products()
    {
        using var db = TestDb.Create();
        TestDb.AddProduct(db, "https://www.noon.com/egypt-en/real-thing/N70294248V/p/", "A real product");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSeeder.RunAsync(db, Now));

        Assert.Contains("not demo data", error.Message);
        Assert.Equal(1, db.Products.Count());
    }

    [Fact]
    public async Task Never_deletes_a_real_product_even_next_to_demo_ones()
    {
        using var db = TestDb.Create();
        await DemoSeeder.RunAsync(db, Now);
        var real = new Product { Url = "https://www.noon.com/egypt-en/real/N1ABCDE/p/", NoonProductId = "N1ABCDE", Source = ProductSource.UserAdded };
        db.Products.Add(real);
        db.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSeeder.RunAsync(db, Now));

        Assert.True(db.Products.Any(p => p.NoonProductId == "N1ABCDE"));
        Assert.Equal(37, db.Products.Count());
    }
}
