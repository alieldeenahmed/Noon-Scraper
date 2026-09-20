using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

// The restock and fake-discount rules, on real PostgreSQL. "Now" is always passed
// in, so a test that is about how old the history is doesn't depend on the clock.
public class PriceHistoryAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset DaysAgo(int days) => Now.AddDays(-days);

    public class IsRestock
    {
        [Fact]
        public async Task False_when_the_product_is_still_out_of_stock()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: DaysAgo(1));

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
            TestDb.AddSnapshot(db, product, 100, stock: true, crawledAt: DaysAgo(1));

            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task True_when_the_last_snapshot_was_out_of_stock_and_it_is_now_in_stock()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: DaysAgo(1));

            Assert.True(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task Only_the_most_recent_snapshot_counts()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 100, stock: true, crawledAt: DaysAgo(1));

            // Out of stock once, but back in stock since - staying in stock isn't a restock.
            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task Ignores_other_products_snapshots()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db, url: "https://www.noon.com/egypt-en/a/N1/p/");
            var other = TestDb.AddProduct(db, url: "https://www.noon.com/egypt-en/b/N2/p/");
            TestDb.AddSnapshot(db, other, 100, stock: false, crawledAt: DaysAgo(1));

            Assert.False(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        // out -> in -> out -> in: two separate restocks, each judged only against
        // the snapshot right before it. This walks the crawl sequence the way the
        // recorder does.
        [Fact]
        public async Task Repeated_stock_flapping_is_a_restock_each_time_it_comes_back()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            var observed = new List<bool>();

            foreach (var (stock, day) in new[] { (false, 5), (true, 4), (false, 3), (true, 2), (true, 1) })
            {
                observed.Add(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, stock));
                TestDb.AddSnapshot(db, product, 100, stock, DaysAgo(day));
            }

            // first crawl (no history), restock, out of stock (not a restock),
            // restock again, and staying in stock.
            Assert.Equal([false, true, false, true, false], observed);
        }

        [Fact]
        public async Task Two_snapshots_at_the_same_instant_resolve_by_insertion_order()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: true, crawledAt: DaysAgo(1));
            // Same timestamp, inserted second - so it is "later": out of stock.
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: DaysAgo(1));

            Assert.True(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
        }

        [Fact]
        public async Task An_old_out_of_stock_snapshot_still_counts_when_it_is_the_latest()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, stock: false, crawledAt: DaysAgo(400));

            // Restock detection has no lookback window: the last thing we knew was
            // out of stock, however long ago.
            Assert.True(await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, newStock: true));
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

        private static Task<DiscountFlag?> DetectAsync(
            AppDbContext db, Product product, decimal newPrice, decimal? discount = 30, int lookbackDays = PriceHistoryAnalyzer.DefaultLookbackDays) =>
            PriceHistoryAnalyzer.DetectFakeDiscountAsync(
                db, product, Trigger(), newPrice, discount, Now, lookbackDays: lookbackDays);

        [Fact]
        public async Task Null_when_there_is_no_advertised_discount()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            Assert.Null(await DetectAsync(db, product, 140, discount: null));
        }

        [Fact]
        public async Task Null_for_an_unsaved_product()
        {
            using var db = TestDb.Create();
            var product = new Product { Url = "https://www.noon.com/egypt-en/a/N1/p/", Source = ProductSource.Seed };

            Assert.Null(await DetectAsync(db, product, 140));
        }

        [Fact]
        public async Task Null_on_the_first_crawl()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);

            Assert.Null(await DetectAsync(db, product, 140));
        }

        [Fact]
        public async Task Null_with_a_single_prior_point()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            // One prior snapshot is both the low and the most recent, so it can't be a
            // spike over itself - there is nothing to compare a "before" price to.
            Assert.Null(await DetectAsync(db, product, 140));
        }

        [Fact]
        public async Task Flags_a_discount_that_follows_a_price_spike_but_never_beats_the_historical_low()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            var spike = TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            var flag = await DetectAsync(db, product, 140);

            Assert.NotNull(flag);
            Assert.Equal(product.Id, flag.ProductId);
            Assert.Equal(999, flag.TriggeringSnapshotId);
            Assert.Equal(150, flag.PriorHighPrice);
            Assert.Equal(spike.CrawledAt, flag.PriorHighDetectedAt);
            Assert.Equal(100, flag.HistoricalLowPrice);
            Assert.Equal(140, flag.DiscountedPrice);
            Assert.Equal(30, flag.DiscountPercent);
            Assert.Equal(Now, flag.DetectedAt);
        }

        [Fact]
        public async Task Null_when_the_new_price_genuinely_beats_the_historical_low()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            Assert.Null(await DetectAsync(db, product, 90, discount: 40));
        }

        [Fact]
        public async Task Null_when_the_last_price_was_not_a_spike_over_the_low()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 105, crawledAt: DaysAgo(1));

            Assert.Null(await DetectAsync(db, product, 100, discount: 5));
        }

        [Fact]
        public async Task Null_when_every_price_so_far_is_equal()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(3));
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(1));

            Assert.Null(await DetectAsync(db, product, 100, discount: 10));
        }

        [Fact]
        public async Task A_spike_of_exactly_ten_percent_counts()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 110, crawledAt: DaysAgo(1));

            Assert.NotNull(await DetectAsync(db, product, 105, discount: 5));
        }

        [Fact]
        public async Task Just_under_a_ten_percent_spike_does_not_count()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 109.99m, crawledAt: DaysAgo(1));

            Assert.Null(await DetectAsync(db, product, 105, discount: 5));
        }

        [Fact]
        public async Task A_new_price_equal_to_the_historical_low_is_still_flagged()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            // Merely returning to the old price isn't a discount worth advertising as one.
            Assert.NotNull(await DetectAsync(db, product, 100, discount: 33));
        }

        [Fact]
        public async Task A_large_spike_is_flagged_all_the_same()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 1000, crawledAt: DaysAgo(1));

            var flag = await DetectAsync(db, product, 900, discount: 10);

            Assert.NotNull(flag);
            Assert.Equal(1000, flag.PriorHighPrice);
        }

        [Fact]
        public async Task Only_the_most_recent_snapshot_is_checked_for_the_spike()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(3));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 101, crawledAt: DaysAgo(1));

            // The spike already came back down - today's 101 isn't following one.
            Assert.Null(await DetectAsync(db, product, 100, discount: 1));
        }

        // The low is recomputed from the history each time, so it moves as the
        // history does: today's "new low" becomes tomorrow's baseline.
        [Fact]
        public async Task The_historical_low_follows_the_history_as_new_crawls_are_recorded()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(4));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(3));

            // 120 is above the low of 100: flagged against it.
            var before = await DetectAsync(db, product, 120, discount: 20);
            Assert.Equal(100, before!.HistoricalLowPrice);

            // The store then genuinely drops to 80, and the price climbs back up.
            TestDb.AddSnapshot(db, product, 80, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 130, crawledAt: DaysAgo(1));

            // Same 120 "discount" now: the low is 80, the previous price 130 is a spike
            // over it, and 120 still doesn't beat 80.
            var after = await DetectAsync(db, product, 120, discount: 20);
            Assert.Equal(80, after!.HistoricalLowPrice);
            Assert.Equal(130, after.PriorHighPrice);
        }

        [Fact]
        public async Task A_low_older_than_the_lookback_window_is_forgotten()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 50, crawledAt: DaysAgo(400));      // long ago
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(10));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            // Over the whole history the low would be 50, and a price of 90 doesn't beat
            // it (it would be flagged). Over the window the low is 100, and 90 does
            // beat that - a genuine discount, so no flag.
            Assert.Null(await DetectAsync(db, product, 90, discount: 40));
        }

        [Fact]
        public async Task An_old_low_does_not_make_a_recent_price_look_inflated_forever()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 50, crawledAt: DaysAgo(400));
            TestDb.AddSnapshot(db, product, 200, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, product, 200, crawledAt: DaysAgo(1));

            // Over the full history the previous 200 is 4x the low of 50, so a 180
            // "discount" would be flagged. Over the recent window it's a flat 200: not a spike.
            Assert.Null(await DetectAsync(db, product, 180, discount: 10));
        }

        [Fact]
        public async Task Null_when_there_is_no_history_inside_the_window()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(300));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(200));

            Assert.Null(await DetectAsync(db, product, 140));
        }

        [Fact]
        public async Task A_snapshot_exactly_on_the_window_edge_is_included()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(PriceHistoryAnalyzer.DefaultLookbackDays));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            Assert.NotNull(await DetectAsync(db, product, 140));
        }

        [Fact]
        public async Task Two_snapshots_at_the_same_instant_resolve_by_insertion_order()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(3));
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(1));
            // Same timestamp; inserted second, so this is the "most recent": the spike.
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(1));

            var flag = await DetectAsync(db, product, 140);

            Assert.NotNull(flag);
            Assert.Equal(150, flag.PriorHighPrice);
        }

        [Fact]
        public async Task Gaps_in_the_history_do_not_matter()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 100, crawledAt: DaysAgo(60));
            TestDb.AddSnapshot(db, product, 150, crawledAt: DaysAgo(30));

            // Nothing for a month: the most recent snapshot is still the 150.
            Assert.NotNull(await DetectAsync(db, product, 140));
        }

        [Fact]
        public async Task Other_products_history_is_ignored()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db, url: "https://www.noon.com/egypt-en/a/N1/p/");
            var other = TestDb.AddProduct(db, url: "https://www.noon.com/egypt-en/b/N2/p/");
            TestDb.AddSnapshot(db, other, 100, crawledAt: DaysAgo(2));
            TestDb.AddSnapshot(db, other, 150, crawledAt: DaysAgo(1));
            TestDb.AddSnapshot(db, product, 140, crawledAt: DaysAgo(1));

            Assert.Null(await DetectAsync(db, product, 140));
        }
    }
}
