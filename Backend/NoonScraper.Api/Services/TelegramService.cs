using System.Net.Http.Json;

namespace NoonScraper.Api.Services;

// Sends messages via Telegram's Bot API. The bot token is optional at
// runtime rather than required at startup - a deployment with Telegram not
// yet configured should keep serving every other endpoint normally, not crash.
public class TelegramService(HttpClient httpClient, IConfiguration configuration, ILogger<TelegramService> logger)
{
    public async Task SendMessageAsync(long chatId, string text)
    {
        // Falls back to a flat variable name for the same reason the DB
        // connection string does - Back4app's environment-variable UI
        // rejects the "__" hierarchical naming .NET's standard config
        // convention would otherwise need.
        var token = configuration["Telegram:BotToken"] ?? configuration["TELEGRAM_BOT_TOKEN"];
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        // A failed send (bad chat id, Telegram outage, etc.) shouldn't take
        // down the caller - the webhook still needs to return 200 so Telegram
        // doesn't keep redelivering the same update, and a subscription that
        // already saved successfully shouldn't be reported as a failure just
        // because the confirmation message didn't go out.
        try
        {
            var response = await httpClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = chatId, text });

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send Telegram message to chat {ChatId}", chatId);
        }
    }
}
