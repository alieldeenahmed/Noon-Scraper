using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Dtos;
using NoonScraper.Api.Services;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Api.Controllers;

// Receives updates from Telegram's webhook - this is how a user "subscribes"
// to a product: the frontend links to https://t.me/<bot>?start=<productId>,
// which Telegram turns into a "/start <productId>" message the moment the
// user taps Start, delivered here with their chat id already attached. No
// manual chat-id entry, and no way for the bot to message someone who hasn't
// messaged it first (a Telegram platform rule, not a choice made here).
[ApiController]
[Route("api/telegram")]
public class TelegramController(AppDbContext db, IConfiguration configuration, TelegramService telegram) : ControllerBase
{
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] TelegramUpdateDto update)
    {
        // Telegram sends this header back on every webhook call once it's set
        // via setWebhook's secret_token param - checking it stops anyone else
        // from posting fake subscription events at this endpoint.
        var expectedSecret = configuration["Telegram:WebhookSecret"] ?? configuration["TELEGRAM_WEBHOOK_SECRET"];
        if (!string.IsNullOrEmpty(expectedSecret) &&
            Request.Headers["X-Telegram-Bot-Api-Secret-Token"] != expectedSecret)
        {
            return Unauthorized();
        }

        var text = update.Message?.Text;
        if (text is null)
        {
            return Ok();
        }

        var chatId = update.Message!.Chat.Id;

        if (text.StartsWith("/start"))
        {
            await HandleStartAsync(chatId, text);
        }
        else if (text == "/stop")
        {
            await HandleStopAsync(chatId);
        }

        return Ok();
    }

    private async Task HandleStartAsync(long chatId, string text)
    {
        var parts = text.Split(' ', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var productId))
        {
            await telegram.SendMessageAsync(chatId,
                "Use the \"Notify me\" button on a product page to subscribe.");
            return;
        }

        var product = await db.Products.FindAsync(productId);
        if (product is null)
        {
            await telegram.SendMessageAsync(chatId, "That product doesn't exist.");
            return;
        }

        var alreadySubscribed = await db.NotificationSubscriptions
            .AnyAsync(s => s.ProductId == productId && s.TelegramChatId == chatId);

        if (!alreadySubscribed)
        {
            // Baseline the subscription against the current price rather than
            // leaving LastNotifiedPrice null - otherwise the very next crawl
            // would have nothing to compare against to decide if the price
            // "dropped".
            var latestPrice = await db.PriceSnapshots
                .Where(s => s.ProductId == productId)
                .OrderByDescending(s => s.CrawledAt)
                .Select(s => (decimal?)s.Price)
                .FirstOrDefaultAsync();

            db.NotificationSubscriptions.Add(new NotificationSubscription
            {
                ProductId = productId,
                TelegramChatId = chatId,
                LastNotifiedPrice = latestPrice
            });
            await db.SaveChangesAsync();
        }

        await telegram.SendMessageAsync(chatId,
            $"You're now tracking \"{product.Name ?? product.Url}\" — I'll message you here on a restock or price drop. Send /stop to unsubscribe from everything.");
    }

    private async Task HandleStopAsync(long chatId)
    {
        var subscriptions = await db.NotificationSubscriptions
            .Where(s => s.TelegramChatId == chatId)
            .ToListAsync();

        db.NotificationSubscriptions.RemoveRange(subscriptions);
        await db.SaveChangesAsync();

        await telegram.SendMessageAsync(chatId, $"Unsubscribed from all {subscriptions.Count} product(s).");
    }
}
