using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Dtos;
using NoonScraper.Data;
using NoonScraper.Data.Configuration;
using NoonScraper.Data.Models;
using NoonScraper.Data.Notifications;

namespace NoonScraper.Api.Controllers;

// Receives updates from Telegram's webhook - this is how a user "subscribes"
// to a product: the frontend links to https://t.me/<bot>?start=<productId>,
// which Telegram turns into a "/start <productId>" message the moment the
// user taps Start, delivered here with their chat id already attached. No
// manual chat-id entry, and no way for the bot to message someone who hasn't
// messaged it first (a Telegram platform rule, not a choice made here).
//
// This is a public URL that anyone on the internet can POST to. The only thing
// separating Telegram from everyone else is the shared secret Telegram echoes back
// in a header, so it is required (the endpoint is off without one, except in local
// development), compared in constant time, and everything after it treats the
// payload as untrusted: chat and text may be missing, only private chats are
// served, and one chat can only hold so many subscriptions.
[ApiController]
[Route("api/telegram")]
public class TelegramController(
    AppDbContext db,
    IConfiguration configuration,
    IHostEnvironment environment,
    ITelegramSender telegram,
    ILogger<TelegramController> logger) : ControllerBase
{
    public const int MaxSubscriptionsPerChat = 25;

    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] TelegramUpdateDto update, CancellationToken ct)
    {
        var rejection = CheckSecret();
        if (rejection is not null)
        {
            return rejection;
        }

        // Anything that isn't a text message in a private chat is acknowledged and
        // ignored - a non-2xx would make Telegram redeliver it.
        if (update.Message is not { Text: { } text, Chat: { Type: "private" } chat })
        {
            return Ok();
        }

        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return Ok();
        }

        // "/start@MyBot 5" is how Telegram sends a command that names the bot.
        var command = parts[0].Split('@')[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : null;

        switch (command)
        {
            case "/start":
                await HandleStartAsync(chat.Id, argument, ct);
                break;

            case "/stop":
                await HandleStopAsync(chat.Id, ct);
                break;
        }

        return Ok();
    }

    // Fails closed: with no secret configured there is nothing to authenticate
    // Telegram against, so the endpoint refuses rather than accepting anyone.
    private IActionResult? CheckSecret()
    {
        var expected = configuration.GetTelegramWebhookSecret();
        if (string.IsNullOrEmpty(expected))
        {
            if (environment.IsDevelopment())
            {
                return null;
            }

            logger.LogError("Telegram webhook called but no webhook secret is configured; refusing the request");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var supplied = Request.Headers[SecretHeader].ToString();
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));

        return matches ? null : Unauthorized();
    }

    private async Task HandleStartAsync(long chatId, string? argument, CancellationToken ct)
    {
        if (!int.TryParse(argument, out var productId) || productId <= 0)
        {
            await telegram.SendAsync(chatId, "Use the \"Notify me\" button on a product page to subscribe.", ct);
            return;
        }

        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId && p.IsActive)
            .Select(p => new { p.Name, p.Url })
            .FirstOrDefaultAsync(ct);

        if (product is null)
        {
            await telegram.SendAsync(chatId, "That product doesn't exist.", ct);
            return;
        }

        var alreadySubscribed = await db.NotificationSubscriptions
            .AnyAsync(s => s.ProductId == productId && s.TelegramChatId == chatId, ct);

        if (!alreadySubscribed)
        {
            var held = await db.NotificationSubscriptions.CountAsync(s => s.TelegramChatId == chatId, ct);
            if (held >= MaxSubscriptionsPerChat)
            {
                await telegram.SendAsync(chatId,
                    $"You're already tracking the maximum of {MaxSubscriptionsPerChat} products. Send /stop to clear them and start over.", ct);
                return;
            }

            // Baseline the subscription against the current price rather than
            // leaving LastNotifiedPrice null - otherwise the very next crawl
            // would have nothing to compare against to decide if the price
            // "dropped".
            var latestPrice = await db.PriceSnapshots
                .Where(s => s.ProductId == productId)
                .OrderByDescending(s => s.CrawledAt)
                .ThenByDescending(s => s.Id)
                .Select(s => (decimal?)s.Price)
                .FirstOrDefaultAsync(ct);

            db.NotificationSubscriptions.Add(new NotificationSubscription
            {
                ProductId = productId,
                TelegramChatId = chatId,
                LastNotifiedPrice = latestPrice
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (DatabaseErrors.IsUniqueViolation(ex))
            {
                // The same /start delivered twice at once (Telegram redelivers):
                // the other one won, which is the outcome we wanted anyway.
                logger.LogInformation("Chat {ChatId} was subscribed to product {ProductId} concurrently", chatId, productId);
            }
        }

        await telegram.SendAsync(chatId,
            $"You're now tracking \"{product.Name ?? product.Url}\" — I'll message you here on a restock or price drop. Send /stop to unsubscribe from everything.", ct);
    }

    private async Task HandleStopAsync(long chatId, CancellationToken ct)
    {
        var removed = await db.NotificationSubscriptions
            .Where(s => s.TelegramChatId == chatId)
            .ExecuteDeleteAsync(ct);

        await telegram.SendAsync(chatId, $"Unsubscribed from all {removed} product(s).", ct);
    }
}
