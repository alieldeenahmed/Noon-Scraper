using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NoonScraper.Crawler;
using NoonScraper.Data;
using NoonScraper.Data.Models;
using NoonScraper.Data.Notifications;

namespace NoonScraper.Tests;

public class ProductRecorderTests
{
    private static Task<RecordResult> Record(
        CrawlHarness h, ScrapedProduct item, Category? category = null, ProductRecorder? recorder = null) =>
        (recorder ?? h.NewRecorder()).RecordAsync(item, ProductSource.Seed, category);

    private static async Task Later(CrawlHarness h, TimeSpan? by = null)
    {
        h.Clock.Advance(by ?? TimeSpan.FromHours(6));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Creates_the_product_and_its_first_snapshot()
    {
        using var h = new CrawlHarness();

        var result = await Record(h, CrawlHarness.Scraped("N1", 100), Category.Laptops);

        Assert.True(result.Created);
        Assert.False(result.Restocked);
        Assert.False(result.DiscountFlagged);

        var product = h.Query(db => db.Products.Single());
        Assert.Equal("Item N1", product.Name);
        Assert.Equal("N1", product.NoonProductId);
        Assert.Equal(Category.Laptops, product.Category);
        Assert.Equal("noon", product.MerchantName);
        Assert.Equal(ProductSource.Seed, product.Source);

        var snapshot = h.Query(db => db.PriceSnapshots.Single());
        Assert.Equal(100, snapshot.Price);
        Assert.Equal(h.Clock.GetUtcNow(), snapshot.CrawledAt);
        Assert.Empty(h.Query(db => db.RestockEvents.ToList()));
        Assert.Empty(h.Query(db => db.DiscountFlags.ToList()));
    }

    [Fact]
    public async Task A_second_crawl_adds_a_snapshot_instead_of_a_duplicate_product()
    {
        using var h = new CrawlHarness();
        var recorder = h.NewRecorder();

        var first = await Record(h, CrawlHarness.Scraped("N1", 100), recorder: recorder);
        await Later(h);
        var second = await Record(h, CrawlHarness.Scraped("N1", 95), recorder: recorder);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.ProductId, second.ProductId);
        Assert.Equal(1, h.Query(db => db.Products.Count()));
        Assert.Equal(2, h.Query(db => db.PriceSnapshots.Count()));
    }

    [Fact]
    public async Task A_sparse_reading_does_not_erase_what_an_earlier_one_found()
    {
        using var h = new CrawlHarness();
        var recorder = h.NewRecorder();
        await Record(h, CrawlHarness.Scraped("N1"), Category.Mobiles, recorder);

        var sparse = CrawlHarness.Scraped("N1");
        sparse.Rating = null;
        sparse.MerchantName = null;
        await Record(h, sparse, category: null, recorder);

        var product = h.Query(db => db.Products.Single());
        Assert.Equal(Category.Mobiles, product.Category);
        Assert.Equal(4.5m, product.Rating);
        Assert.Equal("noon", product.MerchantName);
    }

    [Fact]
    public async Task Records_a_restock_event_when_stock_flips_back_on()
    {
        using var h = new CrawlHarness();
        var recorder = h.NewRecorder();

        await Record(h, CrawlHarness.Scraped("N1", stock: false), recorder: recorder);
        await Later(h);
        var second = await Record(h, CrawlHarness.Scraped("N1", stock: true), recorder: recorder);

        Assert.True(second.Restocked);
        var restock = h.Query(db => db.RestockEvents.Single());
        Assert.Equal(second.SnapshotId, restock.TriggeringSnapshotId);
    }

    [Fact]
    public async Task Raises_a_discount_flag_for_a_discount_that_follows_a_spike()
    {
        using var h = new CrawlHarness();
        var recorder = h.NewRecorder();

        await Record(h, CrawlHarness.Scraped("N1", 100), recorder: recorder);
        await Later(h);
        await Record(h, CrawlHarness.Scraped("N1", 150), recorder: recorder);
        await Later(h);
        var third = await Record(h, CrawlHarness.Scraped("N1", 140, discount: 30), recorder: recorder);

        Assert.True(third.DiscountFlagged);
        var flag = h.Query(db => db.DiscountFlags.Single());
        Assert.Equal(150, flag.PriorHighPrice);
        Assert.Equal(100, flag.HistoricalLowPrice);
        Assert.Equal(140, flag.DiscountedPrice);
        Assert.Equal(third.SnapshotId, flag.TriggeringSnapshotId);
    }

    [Fact]
    public async Task Does_not_flag_a_genuine_discount()
    {
        using var h = new CrawlHarness();
        var recorder = h.NewRecorder();

        await Record(h, CrawlHarness.Scraped("N1", 100), recorder: recorder);
        await Later(h);
        await Record(h, CrawlHarness.Scraped("N1", 150), recorder: recorder);
        await Later(h);
        await Record(h, CrawlHarness.Scraped("N1", 80, discount: 47), recorder: recorder);

        Assert.Empty(h.Query(db => db.DiscountFlags.ToList()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Refuses_a_non_positive_price_and_writes_nothing(int price)
    {
        using var h = new CrawlHarness();

        await Assert.ThrowsAsync<ScrapeParseException>(() => Record(h, CrawlHarness.Scraped("N1", price)));

        Assert.Empty(h.Query(db => db.Products.ToList()));
        Assert.Empty(h.Query(db => db.PriceSnapshots.ToList()));
    }

    [Fact]
    public async Task Refuses_a_product_with_no_name()
    {
        using var h = new CrawlHarness();
        var item = CrawlHarness.Scraped("N1");
        item.Name = "  ";

        await Assert.ThrowsAsync<ScrapeParseException>(() => Record(h, item));

        Assert.Empty(h.Query(db => db.Products.ToList()));
    }

    // ---- partial writes ----

    // A database failure while recording must not leave half a reading behind: no
    // snapshot without its restock event or discount flag, no orphaned product.
    // The failure is injected with a trigger that refuses to insert a flag.
    [Fact]
    public async Task A_failure_part_way_through_leaves_nothing_behind()
    {
        using var h = new CrawlHarness();
        var recorder = h.NewRecorder();
        await Record(h, CrawlHarness.Scraped("N1", 100), recorder: recorder);
        await Later(h);
        await Record(h, CrawlHarness.Scraped("N1", 150), recorder: recorder);
        await Later(h);

        h.Db.Database.ExecuteSqlRaw("""
            CREATE FUNCTION refuse_flags() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'flag insert refused'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_flags BEFORE INSERT ON "DiscountFlags"
            FOR EACH ROW EXECUTE FUNCTION refuse_flags();
            """);

        // This reading would be flagged, so the insert that fails is the last one in
        // the unit of work - after the snapshot was already added.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Record(h, CrawlHarness.Scraped("N1", 140, discount: 30), recorder: recorder));

        Assert.Equal(2, h.Query(db => db.PriceSnapshots.Count()));
        Assert.Empty(h.Query(db => db.DiscountFlags.ToList()));
    }

    [Fact]
    public async Task A_failed_record_does_not_poison_the_next_one_on_the_same_context()
    {
        using var h = new CrawlHarness();
        var recorder = h.NewRecorder();
        await Record(h, CrawlHarness.Scraped("N1", 100), recorder: recorder);
        await Later(h);
        await Record(h, CrawlHarness.Scraped("N1", 150), recorder: recorder);
        await Later(h);

        h.Db.Database.ExecuteSqlRaw("""
            CREATE FUNCTION refuse_flags() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'flag insert refused'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_flags BEFORE INSERT ON "DiscountFlags"
            FOR EACH ROW EXECUTE FUNCTION refuse_flags();
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Record(h, CrawlHarness.Scraped("N1", 140, discount: 30), recorder: recorder));

        // Same context, a different product: unaffected by the failed attempt's
        // leftover tracked entities.
        var other = await Record(h, CrawlHarness.Scraped("N2", 50), recorder: recorder);

        Assert.True(other.Created);
        Assert.Equal(2, h.Query(db => db.Products.Count()));
    }

    // ---- concurrency ----

    [Fact]
    public async Task Concurrent_recordings_of_a_new_product_create_it_once()
    {
        using var h = new CrawlHarness();
        var item = CrawlHarness.Scraped("N1", 100);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => h.NewRecorder(h.NewContext()).RecordAsync(item, ProductSource.Seed, null))));

        Assert.Equal(1, h.Query(db => db.Products.Count()));
        Assert.Equal(8, h.Query(db => db.PriceSnapshots.Count()));
        Assert.Equal(1, results.Count(r => r.Created));
    }

    // The property the advisory lock exists for. Six crawls of a product that was
    // out of stock all see it in stock at once: without serialization each of them
    // would compare against the same out-of-stock snapshot and record its own
    // restock. With it, only the first does - the rest see it was already restocked.
    [Fact]
    public async Task Concurrent_crawls_record_a_restock_only_once()
    {
        using var h = new CrawlHarness();
        await Record(h, CrawlHarness.Scraped("N1", 100, stock: false));
        await Later(h);

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => h.NewRecorder(h.NewContext())
                .RecordAsync(CrawlHarness.Scraped("N1", 100, stock: true), ProductSource.Seed, null))));

        Assert.Equal(1, results.Count(r => r.Restocked));
        Assert.Equal(1, h.Query(db => db.RestockEvents.Count()));
        Assert.Equal(7, h.Query(db => db.PriceSnapshots.Count()));
    }

    [Fact]
    public async Task Concurrent_crawls_of_different_products_do_not_block_each_other()
    {
        using var h = new CrawlHarness();

        var results = await Task.WhenAll(Enumerable.Range(1, 8)
            .Select(i => Task.Run(() => h.NewRecorder(h.NewContext())
                .RecordAsync(CrawlHarness.Scraped($"N{i}", 100), ProductSource.Seed, null))));

        Assert.All(results, r => Assert.True(r.Created));
        Assert.Equal(8, h.Query(db => db.Products.Count()));
    }

    // The API inserts a submitted product without taking the advisory lock, so the
    // crawler can find no row, decide to create one, and lose to the API's insert.
    // The unique index turns that into a retry as an update - not a failed crawl.
    [Fact]
    public async Task Losing_a_race_with_the_api_insert_falls_back_to_an_update()
    {
        using var h = new CrawlHarness();
        var item = CrawlHarness.Scraped("N1", 100);
        var interceptor = new InsertProductBeforeFirstSave(h.ConnectionString, item.Url);
        using var racing = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(h.ConnectionString).AddInterceptors(interceptor).Options);

        var result = await h.NewRecorder(racing).RecordAsync(item, ProductSource.UserAdded, null);

        Assert.True(interceptor.Fired);
        Assert.False(result.Created);
        Assert.Equal(1, h.Query(db => db.Products.Count()));
        Assert.Equal("Item N1", h.Query(db => db.Products.Single()).Name);
        Assert.Equal(1, h.Query(db => db.PriceSnapshots.Count()));
    }

    // ---- notification ordering ----

    [Fact]
    public async Task Subscribers_are_notified_only_after_the_reading_is_committed()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N1", ProductSource.Seed);
        h.Db.NotificationSubscriptions.Add(new NotificationSubscription
        {
            ProductId = product.Id, TelegramChatId = 7, LastNotifiedPrice = 100
        });
        h.Db.SaveChanges();
        var probe = new CommitProbeSender(h);

        await h.NewRecorder(sender: probe).RecordAsync(CrawlHarness.Scraped("N1", 90), ProductSource.Seed, null);

        Assert.Equal(1, probe.SnapshotsVisibleWhenSending);
    }

    [Fact]
    public async Task A_notification_failure_does_not_fail_the_recording()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N1", ProductSource.Seed);
        h.Db.NotificationSubscriptions.Add(new NotificationSubscription
        {
            ProductId = product.Id, TelegramChatId = 7, LastNotifiedPrice = 100
        });
        h.Db.SaveChanges();
        var exploding = new ThrowingSender();

        var result = await h.NewRecorder(sender: exploding).RecordAsync(CrawlHarness.Scraped("N1", 90), ProductSource.Seed, null);

        Assert.Equal(1, h.Query(db => db.PriceSnapshots.Count()));
        Assert.Equal(0, result.Notifications.Sent);
    }

    private sealed class InsertProductBeforeFirstSave(string connectionString, string url) : SaveChangesInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    "INSERT INTO \"Products\" (\"Url\", \"Source\", \"IsActive\", \"AddedAt\") VALUES (@url, 1, true, now())", connection);
                command.Parameters.AddWithValue("url", url);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class CommitProbeSender(CrawlHarness harness) : ITelegramSender
    {
        public int SnapshotsVisibleWhenSending { get; private set; } = -1;

        public bool IsConfigured => true;

        public Task<SendOutcome> SendAsync(long chatId, string text, CancellationToken ct = default)
        {
            SnapshotsVisibleWhenSending = harness.Query(db => db.PriceSnapshots.Count());
            return Task.FromResult(SendOutcome.Sent);
        }
    }

    private sealed class ThrowingSender : ITelegramSender
    {
        public bool IsConfigured => true;

        public Task<SendOutcome> SendAsync(long chatId, string text, CancellationToken ct = default) =>
            throw new InvalidOperationException("network on fire");
    }
}
