using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Crawler;

// The crawler is the only place that ever learns a product restocked or
// dropped in price, so it's the natural place to actually send the Telegram
// message too - the API only handles the subscribe/unsubscribe side, via
// its webhook.
public static class TelegramNotifier
{
    public static async Task NotifySubscribersAsync(
        AppDbContext db, HttpClient httpClient, string? botToken,
        Product product, bool isRestock, decimal newPrice)
    {
        if (string.IsNullOrEmpty(botToken))
        {
            return;
        }

        var subscriptions = await db.NotificationSubscriptions
            .Where(s => s.ProductId == product.Id)
            .ToListAsync();

        foreach (var subscription in subscriptions)
        {
            // No baseline yet (e.g. subscribed before this product had ever
            // been crawled) - establish one silently rather than treating an
            // unknown prior price as a "drop".
            if (subscription.LastNotifiedPrice is null)
            {
                subscription.LastNotifiedPrice = newPrice;
                continue;
            }

            var priceDropped = newPrice < subscription.LastNotifiedPrice;
            if (!isRestock && !priceDropped)
            {
                continue;
            }

            var reason = isRestock ? "is back in stock" : $"dropped to {newPrice:0.##}";
            var text = $"{product.Name ?? product.Url} {reason}\n{product.Url}";

            try
            {
                var response = await httpClient.PostAsJsonAsync(
                    $"https://api.telegram.org/bot{botToken}/sendMessage",
                    new { chat_id = subscription.TelegramChatId, text });
                response.EnsureSuccessStatusCode();

                subscription.LastNotifiedPrice = newPrice;
                subscription.LastNotifiedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed to notify chat {subscription.TelegramChatId}: {ex.Message}");
            }
        }
    }
}
