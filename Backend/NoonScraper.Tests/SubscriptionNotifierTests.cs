using Microsoft.EntityFrameworkCore;
using NoonScraper.Data.Models;
using NoonScraper.Data.Notifications;

namespace NoonScraper.Tests;

public class SubscriptionNotifierTests
{
    private static NotificationSubscription Subscribe(
        CrawlHarness h, Product product, long chatId = 42, decimal? baseline = 100, DateTimeOffset? notifiedAt = null)
    {
        var subscription = new NotificationSubscription
        {
            ProductId = product.Id,
            TelegramChatId = chatId,
            LastNotifiedPrice = baseline,
            LastNotifiedAt = notifiedAt
        };
        h.Db.NotificationSubscriptions.Add(subscription);
        h.Db.SaveChanges();
        return subscription;
    }

    private static NotificationSubscription Read(CrawlHarness h, int id) =>
        h.Query(db => db.NotificationSubscriptions.AsNoTracking().Single(s => s.Id == id));

    [Fact]
    public async Task Does_nothing_when_telegram_is_not_configured()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct(name: "Anker Charger");
        var sub = Subscribe(h, product);
        h.Telegram.IsConfigured = false;

        var result = await h.NewNotifier().NotifyAsync(product, 50, isRestock: false);

        Assert.Empty(h.Telegram.Sent);
        Assert.Equal(new NotifyResult(0, 0, 0, 0), result);
        Assert.Equal(100, Read(h, sub.Id).LastNotifiedPrice);
    }

    [Fact]
    public async Task Sets_a_baseline_silently_when_there_is_none_yet()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        var sub = Subscribe(h, product, baseline: null);

        var result = await h.NewNotifier().NotifyAsync(product, 80, isRestock: false);

        Assert.Empty(h.Telegram.Sent);
        Assert.Equal(1, result.Baselined);
        Assert.Equal(80, Read(h, sub.Id).LastNotifiedPrice);
        Assert.Null(Read(h, sub.Id).LastNotifiedAt);
    }

    [Fact]
    public async Task Notifies_on_a_price_drop_and_moves_the_baseline()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct(name: "Anker Charger");
        var sub = Subscribe(h, product);

        var result = await h.NewNotifier().NotifyAsync(product, 90, isRestock: false);

        var (chatId, text) = Assert.Single(h.Telegram.Sent);
        Assert.Equal(42, chatId);
        Assert.Contains("dropped to 90", text);
        Assert.Contains("Anker Charger", text);
        Assert.Equal(1, result.Sent);

        var saved = Read(h, sub.Id);
        Assert.Equal(90, saved.LastNotifiedPrice);
        Assert.Equal(h.Clock.GetUtcNow(), saved.LastNotifiedAt);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    public async Task Stays_quiet_when_the_price_did_not_drop(int newPrice)
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        var sub = Subscribe(h, product);

        await h.NewNotifier().NotifyAsync(product, newPrice, isRestock: false);

        Assert.Empty(h.Telegram.Sent);
        Assert.Equal(100, Read(h, sub.Id).LastNotifiedPrice);
    }

    [Fact]
    public async Task Notifies_on_a_restock_even_if_the_price_did_not_drop()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        Subscribe(h, product);

        await h.NewNotifier().NotifyAsync(product, 100, isRestock: true);

        Assert.Contains("is back in stock", Assert.Single(h.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_restock_that_is_also_a_price_drop_is_one_message()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        Subscribe(h, product);

        await h.NewNotifier().NotifyAsync(product, 70, isRestock: true);

        Assert.Single(h.Telegram.Sent);
    }

    [Fact]
    public async Task The_same_price_seen_again_does_not_notify_twice()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        Subscribe(h, product);
        var notifier = h.NewNotifier();

        await notifier.NotifyAsync(product, 90, isRestock: false);
        await notifier.NotifyAsync(product, 90, isRestock: false);
        await notifier.NotifyAsync(product, 95, isRestock: false);

        Assert.Single(h.Telegram.Sent);
    }

    [Fact]
    public async Task A_further_drop_notifies_again()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        Subscribe(h, product);
        var notifier = h.NewNotifier();

        await notifier.NotifyAsync(product, 90, isRestock: false);
        h.Clock.Advance(TimeSpan.FromDays(1));
        await notifier.NotifyAsync(product, 80, isRestock: false);

        Assert.Equal(2, h.Telegram.Sent.Count);
    }

    [Fact]
    public async Task A_failed_send_releases_the_claim_so_the_next_crawl_retries()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        var sub = Subscribe(h, product);
        h.Telegram.Outcome = _ => SendOutcome.Failed;

        var failed = await h.NewNotifier().NotifyAsync(product, 90, isRestock: false);

        Assert.Equal(1, failed.Failed);
        var afterFailure = Read(h, sub.Id);
        Assert.Equal(100, afterFailure.LastNotifiedPrice);
        Assert.Null(afterFailure.LastNotifiedAt);

        // Telegram recovers; the very next crawl delivers the same alert.
        h.Telegram.Outcome = _ => SendOutcome.Sent;
        var retried = await h.NewNotifier().NotifyAsync(product, 90, isRestock: false);

        Assert.Equal(1, retried.Sent);
        Assert.Single(h.Telegram.Sent);
    }

    [Fact]
    public async Task A_chat_that_blocked_the_bot_is_unsubscribed_instead_of_retried_forever()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        var sub = Subscribe(h, product);
        h.Telegram.Outcome = _ => SendOutcome.Undeliverable;

        var result = await h.NewNotifier().NotifyAsync(product, 90, isRestock: false);

        Assert.Equal(1, result.Removed);
        Assert.False(h.Query(db => db.NotificationSubscriptions.Any(s => s.Id == sub.Id)));
    }

    [Fact]
    public async Task One_undeliverable_subscriber_does_not_stop_the_others()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        Subscribe(h, product, chatId: 1);
        Subscribe(h, product, chatId: 2);
        Subscribe(h, product, chatId: 3);
        h.Telegram.Outcome = chat => chat == 2 ? SendOutcome.Undeliverable : SendOutcome.Sent;

        var result = await h.NewNotifier().NotifyAsync(product, 90, isRestock: false);

        Assert.Equal(2, result.Sent);
        Assert.Equal(1, result.Removed);
        Assert.Equal([1L, 3L], h.Telegram.Sent.Select(s => s.ChatId).Order());
    }

    [Fact]
    public async Task Only_subscribers_of_that_product_are_notified()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N1");
        var other = h.AddProduct("N2");
        Subscribe(h, product, chatId: 1);
        Subscribe(h, other, chatId: 2);

        await h.NewNotifier().NotifyAsync(product, 90, isRestock: false);

        Assert.Equal(1, Assert.Single(h.Telegram.Sent).ChatId);
    }

    // Two crawls overlap (the scheduled run and an on-demand one, or a retried job)
    // and both read the same baseline: exactly one of them may send. The sender is
    // slowed down so both really are past the "should I notify?" decision before
    // either finishes sending.
    [Fact]
    public async Task Concurrent_crawls_do_not_double_send()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        var sub = Subscribe(h, product);
        using var gate = new SemaphoreSlim(0);
        var sender = new SlowSender(gate);

        var first = h.NewNotifier(h.NewContext(), sender).NotifyAsync(product, 90, isRestock: false);
        var second = h.NewNotifier(h.NewContext(), sender).NotifyAsync(product, 90, isRestock: false);

        await Task.Delay(300);
        gate.Release(2);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, sender.Sent);
        Assert.Equal(1, results.Sum(r => r.Sent));
        Assert.Equal(90, Read(h, sub.Id).LastNotifiedPrice);
    }

    [Fact]
    public async Task Concurrent_crawls_deliver_a_failed_send_exactly_once_after_recovery()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        Subscribe(h, product);
        var flaky = new FakeTelegramSender { Outcome = _ => SendOutcome.Failed };

        await Task.WhenAll(
            h.NewNotifier(h.NewContext(), flaky).NotifyAsync(product, 90, isRestock: false),
            h.NewNotifier(h.NewContext(), flaky).NotifyAsync(product, 90, isRestock: false));

        // Whichever attempt lost the claim skipped; the winner failed and released.
        // Nothing was delivered and nothing was lost: the next crawl still sees 100.
        Assert.Empty(flaky.Sent);
        flaky.Outcome = _ => SendOutcome.Sent;
        var recovered = await h.NewNotifier().NotifyAsync(product, 90, isRestock: false);
        Assert.Equal(1, recovered.Sent);
    }

    // Deleting a product must take its subscriptions with it - and a later /start
    // for it must be refused rather than resurrect anything.
    [Fact]
    public void Deleting_a_product_removes_its_subscriptions()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct();
        Subscribe(h, product, chatId: 1);
        Subscribe(h, product, chatId: 2);

        h.Db.Products.Remove(product);
        h.Db.SaveChanges();

        Assert.Empty(h.Query(db => db.NotificationSubscriptions.ToList()));
    }

    private sealed class SlowSender(SemaphoreSlim gate) : ITelegramSender
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public bool IsConfigured => true;

        public async Task<SendOutcome> SendAsync(long chatId, string text, CancellationToken ct = default)
        {
            await gate.WaitAsync(ct);
            Interlocked.Increment(ref _sent);
            return SendOutcome.Sent;
        }
    }
}
