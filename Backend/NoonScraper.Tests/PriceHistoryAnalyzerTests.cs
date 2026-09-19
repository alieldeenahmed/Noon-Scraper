using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

public class PriceHistoryAnalyzerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    public class IsRestock
    {
        [Fact]
        public async Task False_when_the_product_is_still_out_of_stock()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: T0);

            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: false));
        }

        [Fact]
        public async Task False_for_a_product_seen_for_the_first_time()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);

            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task False_for_an_unsaved_product()
        {
            using var db = TestDb.Create();

            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, productId: 0, newStock: true));
        }

        [Fact]
        public async Task False_when_the_product_was_already_in_stock()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: true, crawledAt: T0);

            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task True_when_the_last_snapshot_was_out_of_stock_and_it_is_now_in_stock()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: T0);

            Assert.True(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task Only_the_most_recent_snapshot_counts()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 100, stock: true, crawledAt: T0.AddDays(1));

            // Out of stock once, but back in stock since - staying in stock isn't a restock.
            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task Ignores_other_products_snapshots()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db, url: "https://www.noon.com/a/N1/p/");
            var other = TestDb.AddProduct(db, url: "https://www.noon.com/b/N2/p/");
            TestDb.AddSnapshot(db, other, 100, stock: false, crawledAt: T0);

            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }
    }

    public class DetectFakeDiscount
    {
        private static PriceSnapshot Trigger() => new()
        {
            Id = 999,
            ProductId = 1,
            Price = 0,
            Stock = true
        };

        [Fact]
        public async Task Null_when_there_is_no_advertised_discount()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 150, crawledAt: T0.AddDays(1));

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 140, newDiscountPercent: null);

            Assert.Null(flag);
        }

        [Fact]
        public async Task Null_for_an_unsaved_product()
        {
            using var db = TestDb.Create();
            var product = new Product { Url = "https://www.noon.com/a/N1/p/", Source = ProductSource.Seed };

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 140, newDiscountPercent: 30);

            Assert.Null(flag);
        }

        [Fact]
        public async Task Null_for_a_product_with_no_history()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 140, newDiscountPercent: 30);

            Assert.Null(flag);
        }

        [Fact]
        public async Task Flags_a_discount_that_follows_a_price_spike_but_never_beats_the_historical_low()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            var spike = TestDb.AddSnapshot(db, product, 150, crawledAt: T0.AddDays(1));

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 140, newDiscountPercent: 30);

            Assert.NotNull(flag);
            Assert.Equal(product.Id, flag.ProductId);
            Assert.Equal(999, flag.TriggeringSnapshotId);
            Assert.Equal(150, flag.PriorHighPrice);
            Assert.Equal(spike.CrawledAt, flag.PriorHighDetectedAt);
            Assert.Equal(140, flag.DiscountedPrice);
            Assert.Equal(30, flag.DiscountPercent);
        }

        [Fact]
        public async Task Null_when_the_new_price_genuinely_beats_the_historical_low()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 150, crawledAt: T0.AddDays(1));

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 90, newDiscountPercent: 40);

            Assert.Null(flag);
        }

        [Fact]
        public async Task Null_when_the_last_price_was_not_a_spike_over_the_low()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 105, crawledAt: T0.AddDays(1));

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 100, newDiscountPercent: 5);

            Assert.Null(flag);
        }

        [Fact]
        public async Task A_spike_of_exactly_ten_percent_counts()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 110, crawledAt: T0.AddDays(1));

            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 105, newDiscountPercent: 5);

            Assert.NotNull(flag);
        }

        [Fact]
        public async Task A_new_price_equal_to_the_historical_low_is_still_flagged()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 150, crawledAt: T0.AddDays(1));

            // Merely returning to the old price isn't a discount worth advertising as one.
            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 100, newDiscountPercent: 33);

            Assert.NotNull(flag);
        }

        [Fact]
        public async Task Only_the_most_recent_snapshot_is_checked_for_the_spike()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 150, crawledAt: T0.AddDays(1));
            TestDb.AddSnapshot(db, product, 101, crawledAt: T0.AddDays(2));

            // The spike already came back down - today's 101 isn't following one.
            var flag = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(db, product, Trigger(), 100, newDiscountPercent: 1);

            Assert.Null(flag);
        }
    }
}
