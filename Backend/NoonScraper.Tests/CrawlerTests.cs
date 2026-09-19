using System.Net;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Crawler;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

// Stands in for Telegram's API: records what would have been sent.
internal sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status);
    }
}

public class TelegramNotifierTests
{
    private static (AppDbContext Db, Product Product, NotificationSubscription Sub) Setup(decimal? lastNotifiedPrice)
    {
        var db = TestDb.Create();
        var product = TestDb.AddProduct(db, name: "Anker Charger");
        var sub = new NotificationSubscription
        {
            ProductId = product.Id,
            TelegramChatId = 42,
            LastNotifiedPrice = lastNotifiedPrice
        };
        db.NotificationSubscriptions.Add(sub);
        db.SaveChanges();
        return (db, product, sub);
    }

    [Fact]
    public async Task Does_nothing_without_a_bot_token()
    {
        var (db, product, sub) = Setup(lastNotifiedPrice: 100);
        var handler = new RecordingHandler();

        await TelegramNotifier.NotifySubscribersAsync(db, new HttpClient(handler), botToken: null, product, isRestock: false, newPrice: 50);

        Assert.Empty(handler.Bodies);
        Assert.Equal(100, sub.LastNotifiedPrice);
    }

    [Fact]
    public async Task Sets_a_baseline_silently_when_there_is_none_yet()
    {
        var (db, product, sub) = Setup(lastNotifiedPrice: null);
        var handler = new RecordingHandler();

        await TelegramNotifier.NotifySubscribersAsync(db, new HttpClient(handler), "token", product, isRestock: false, newPrice: 80);

        Assert.Empty(handler.Bodies);
        Assert.Equal(80, sub.LastNotifiedPrice);
    }

    [Fact]
    public async Task Notifies_on_a_price_drop_and_moves_the_baseline()
    {
        var (db, product, sub) = Setup(lastNotifiedPrice: 100);
        var handler = new RecordingHandler();

        await TelegramNotifier.NotifySubscribersAsync(db, new HttpClient(handler), "token", product, isRestock: false, newPrice: 90);

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("dropped to 90", body);
        Assert.Contains("Anker Charger", body);
        Assert.Equal(90, sub.LastNotifiedPrice);
        Assert.NotNull(sub.LastNotifiedAt);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    public async Task Stays_quiet_when_the_price_did_not_drop(decimal newPrice)
    {
        var (db, product, sub) = Setup(lastNotifiedPrice: 100);
        var handler = new RecordingHandler();

        await TelegramNotifier.NotifySubscribersAsync(db, new HttpClient(handler), "token", product, isRestock: false, newPrice);

        Assert.Empty(handler.Bodies);
        Assert.Equal(100, sub.LastNotifiedPrice);
    }

    [Fact]
    public async Task Notifies_on_a_restock_even_if_the_price_did_not_drop()
    {
        var (db, product, _) = Setup(lastNotifiedPrice: 100);
        var handler = new RecordingHandler();

        await TelegramNotifier.NotifySubscribersAsync(db, new HttpClient(handler), "token", product, isRestock: true, newPrice: 100);

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("is back in stock", body);
    }

    [Fact]
    public async Task A_failed_send_does_not_throw_and_keeps_the_old_baseline()
    {
        var (db, product, sub) = Setup(lastNotifiedPrice: 100);
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);

        await TelegramNotifier.NotifySubscribersAsync(db, new HttpClient(handler), "token", product, isRestock: false, newPrice: 90);

        // Baseline must not advance, or the drop would never be retried next crawl.
        Assert.Equal(100, sub.LastNotifiedPrice);
        Assert.Null(sub.LastNotifiedAt);
    }

    [Fact]
    public async Task Only_notifies_subscribers_of_that_product()
    {
        var (db, product, _) = Setup(lastNotifiedPrice: 100);
        var other = TestDb.AddProduct(db, url: "https://www.noon.com/other/N2/p/");
        db.NotificationSubscriptions.Add(new NotificationSubscription
        {
            ProductId = other.Id,
            TelegramChatId = 7,
            LastNotifiedPrice = 100
        });
        db.SaveChanges();
        var handler = new RecordingHandler();

        await TelegramNotifier.NotifySubscribersAsync(db, new HttpClient(handler), "token", product, isRestock: false, newPrice: 90);

        Assert.Single(handler.Bodies);
    }
}

public class ProductUpserterTests
{
    private static ScrapedProduct Scraped(decimal price, bool stock = true, decimal? discount = null) => new()
    {
        NoonProductId = "N1",
        Url = "https://www.noon.com/egypt-en/thing/N1/p/",
        Name = "Thing",
        Price = price,
        Stock = stock,
        DiscountPercent = discount,
        MerchantName = "noon",
        Rating = 4.5m
    };

    private static async Task UpsertAsync(AppDbContext db, ScrapedProduct item, Category? category = null)
    {
        await ProductUpserter.UpsertAsync(
            db, new HttpClient(new RecordingHandler()), telegramBotToken: null, [], item, ProductSource.Seed, category);
        await db.SaveChangesAsync();
        // Real crawls are minutes apart; keep snapshots strictly ordered here.
        await Task.Delay(5);
    }

    [Fact]
    public async Task Creates_the_product_and_its_first_snapshot()
    {
        using var db = TestDb.Create();

        await UpsertAsync(db, Scraped(100), Category.Laptops);

        var product = await db.Products.SingleAsync();
        Assert.Equal("Thing", product.Name);
        Assert.Equal("N1", product.NoonProductId);
        Assert.Equal(Category.Laptops, product.Category);
        Assert.Equal("noon", product.MerchantName);
        var snapshot = await db.PriceSnapshots.SingleAsync();
        Assert.Equal(100, snapshot.Price);
        Assert.Empty(db.RestockEvents);
        Assert.Empty(db.DiscountFlags);
    }

    [Fact]
    public async Task A_second_crawl_adds_a_snapshot_instead_of_a_duplicate_product()
    {
        using var db = TestDb.Create();

        await UpsertAsync(db, Scraped(100));
        await UpsertAsync(db, Scraped(95));

        Assert.Equal(1, await db.Products.CountAsync());
        Assert.Equal(2, await db.PriceSnapshots.CountAsync());
    }

    [Fact]
    public async Task Does_not_overwrite_the_category_when_none_is_supplied()
    {
        using var db = TestDb.Create();

        await UpsertAsync(db, Scraped(100), Category.Mobiles);
        await UpsertAsync(db, Scraped(100), category: null);

        Assert.Equal(Category.Mobiles, (await db.Products.SingleAsync()).Category);
    }

    [Fact]
    public async Task Records_a_restock_event_when_stock_flips_back_on()
    {
        using var db = TestDb.Create();

        await UpsertAsync(db, Scraped(100, stock: false));
        await UpsertAsync(db, Scraped(100, stock: true));

        var restock = await db.RestockEvents.SingleAsync();
        var latest = await db.PriceSnapshots.OrderByDescending(s => s.CrawledAt).FirstAsync();
        Assert.Equal(latest.Id, restock.TriggeringSnapshotId);
    }

    [Fact]
    public async Task Raises_a_discount_flag_for_a_discount_that_follows_a_spike()
    {
        using var db = TestDb.Create();

        await UpsertAsync(db, Scraped(100));
        await UpsertAsync(db, Scraped(150));
        await UpsertAsync(db, Scraped(140, discount: 30));

        var flag = await db.DiscountFlags.SingleAsync();
        Assert.Equal(150, flag.PriorHighPrice);
        Assert.Equal(140, flag.DiscountedPrice);
    }

    [Fact]
    public async Task Does_not_flag_a_genuine_discount()
    {
        using var db = TestDb.Create();

        await UpsertAsync(db, Scraped(100));
        await UpsertAsync(db, Scraped(150));
        await UpsertAsync(db, Scraped(80, discount: 47));

        Assert.Empty(db.DiscountFlags);
    }
}
