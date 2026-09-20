using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoonScraper.Data.Models;

namespace NoonScraper.Data.Notifications;

public sealed record NotifyResult(int Sent, int Failed, int Removed, int Baselined);

// Sends "price dropped / back in stock" messages to a product's subscribers.
//
// Two crawls can touch the same product at once (the scheduled run overlapping an
// on-demand one, a retried job), and both would read the same baseline and both
// decide to notify. So a notification is *claimed* before it's sent: one
// conditional UPDATE that only succeeds for whoever still sees the row exactly as
// they read it. The winner sends; everyone else skips. If the send fails, the
// claim is rolled back so the next crawl retries - never a silent loss, never a
// duplicate.
public sealed class SubscriptionNotifier(
    AppDbContext db,
    ITelegramSender sender,
    TimeProvider clock,
    ILogger<SubscriptionNotifier> logger)
{
    public async Task<NotifyResult> NotifyAsync(
        Product product, decimal newPrice, bool isRestock, CancellationToken ct = default)
    {
        if (!sender.IsConfigured)
        {
            return new NotifyResult(0, 0, 0, 0);
        }

        var subscriptions = await db.NotificationSubscriptions
            .AsNoTracking()
            .Where(s => s.ProductId == product.Id)
            .ToListAsync(ct);

        int sent = 0, failed = 0, removed = 0, baselined = 0;

        foreach (var subscription in subscriptions)
        {
            // No baseline yet (subscribed before the product had ever been
            // crawled): establish one without notifying - an unknown prior price
            // isn't a "drop".
            if (subscription.LastNotifiedPrice is null)
            {
                await db.NotificationSubscriptions
                    .Where(s => s.Id == subscription.Id && s.LastNotifiedPrice == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastNotifiedPrice, (decimal?)newPrice), ct);
                baselined++;
                continue;
            }

            var priceDropped = newPrice < subscription.LastNotifiedPrice;
            if (!isRestock && !priceDropped)
            {
                continue;
            }

            var claimedAt = clock.GetUtcNow();
            if (!await ClaimAsync(subscription, newPrice, claimedAt, ct))
            {
                logger.LogInformation(
                    "Subscription {SubscriptionId} was already notified by a concurrent crawl; skipping", subscription.Id);
                continue;
            }

            var reason = isRestock ? "is back in stock" : $"dropped to {newPrice:0.##}";
            var text = $"{product.Name ?? product.Url} {reason}\n{product.Url}";

            switch (await sender.SendAsync(subscription.TelegramChatId, text, ct))
            {
                case SendOutcome.Sent:
                    sent++;
                    break;

                case SendOutcome.Undeliverable:
                    await db.NotificationSubscriptions.Where(s => s.Id == subscription.Id).ExecuteDeleteAsync(ct);
                    logger.LogInformation(
                        "Chat {ChatId} can no longer receive messages; removed subscription {SubscriptionId}",
                        subscription.TelegramChatId, subscription.Id);
                    removed++;
                    break;

                default:
                    await ReleaseClaimAsync(subscription, newPrice, claimedAt, ct);
                    failed++;
                    break;
            }
        }

        return new NotifyResult(sent, failed, removed, baselined);
    }

    // Succeeds only if the row still has the price and timestamp this crawl read.
    private async Task<bool> ClaimAsync(
        NotificationSubscription read, decimal newPrice, DateTimeOffset claimedAt, CancellationToken ct)
    {
        var rows = await db.NotificationSubscriptions
            .Where(s => s.Id == read.Id
                && s.LastNotifiedPrice == read.LastNotifiedPrice
                && s.LastNotifiedAt == read.LastNotifiedAt)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastNotifiedPrice, (decimal?)newPrice)
                .SetProperty(s => s.LastNotifiedAt, (DateTimeOffset?)claimedAt), ct);
        return rows == 1;
    }

    // Undo a claim whose message never went out, but only if nothing has touched
    // the row since - otherwise a newer notification's state would be overwritten.
    private async Task ReleaseClaimAsync(
        NotificationSubscription read, decimal claimedPrice, DateTimeOffset claimedAt, CancellationToken ct) =>
        await db.NotificationSubscriptions
            .Where(s => s.Id == read.Id
                && s.LastNotifiedPrice == claimedPrice
                && s.LastNotifiedAt == claimedAt)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastNotifiedPrice, read.LastNotifiedPrice)
                .SetProperty(s => s.LastNotifiedAt, read.LastNotifiedAt), ct);
}
