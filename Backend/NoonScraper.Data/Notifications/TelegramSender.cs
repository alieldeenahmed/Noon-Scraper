using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace NoonScraper.Data.Notifications;

public enum SendOutcome
{
    Sent,

    // Might work next time: a timeout, a 5xx, Telegram rate limiting.
    Failed,

    // Never going to work: the user blocked the bot or deleted the chat. The
    // subscription should be dropped instead of retried on every crawl.
    Undeliverable
}

public interface ITelegramSender
{
    // False when no bot token is configured. Callers treat that as "notifications
    // are off", not as an error - the app runs fine without Telegram.
    bool IsConfigured { get; }

    Task<SendOutcome> SendAsync(long chatId, string text, CancellationToken ct = default);
}

// The one place that talks to Telegram's sendMessage. Used by the API (webhook
// confirmations) and the crawler (price-drop / restock alerts), which used to
// carry two near-identical copies.
public sealed class TelegramSender(HttpClient httpClient, string? botToken, ILogger<TelegramSender> logger) : ITelegramSender
{
    public bool IsConfigured => !string.IsNullOrEmpty(botToken);

    public async Task<SendOutcome> SendAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return SendOutcome.Failed;
        }

        try
        {
            // The token is part of Telegram's URL by design, so this URL must never
            // be logged - see the log-level filter on System.Net.Http.HttpClient.
            using var response = await httpClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{botToken}/sendMessage",
                new { chat_id = chatId, text },
                ct);

            if (response.IsSuccessStatusCode)
            {
                return SendOutcome.Sent;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return SendOutcome.Undeliverable;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                body.Contains("chat not found", StringComparison.OrdinalIgnoreCase))
            {
                return SendOutcome.Undeliverable;
            }

            logger.LogWarning(
                "Telegram send to chat {ChatId} failed with HTTP {Status}", chatId, (int)response.StatusCode);
            return SendOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Type and message only - not the exception object, whose stack and
            // request details could include the URL.
            logger.LogWarning("Telegram send to chat {ChatId} failed: {Type}: {Message}", chatId, ex.GetType().Name, ex.Message);
            return SendOutcome.Failed;
        }
    }
}
