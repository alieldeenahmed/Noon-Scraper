using Microsoft.Extensions.Configuration;

namespace NoonScraper.Data.Configuration;

public static class SettingsExtensions
{
    // The standard .NET nested key ("Telegram:BotToken", or Telegram__BotToken as
    // an environment variable) first, then a flat name. The flat name exists
    // because Back4app's environment-variable UI rejects "__" and ":" - see
    // docs/hosting.md. Every such fallback lives here, in one place, instead of
    // being repeated at each call site.
    public static string? GetSetting(this IConfiguration configuration, string key, string flatName) =>
        configuration[key] ?? configuration[flatName];

    public static string? GetDatabaseConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString("DefaultConnection") ?? configuration["DATABASE"];

    public static string? GetTelegramBotToken(this IConfiguration configuration) =>
        configuration.GetSetting("Telegram:BotToken", "TELEGRAM_BOT_TOKEN");

    public static string? GetTelegramWebhookSecret(this IConfiguration configuration) =>
        configuration.GetSetting("Telegram:WebhookSecret", "TELEGRAM_WEBHOOK_SECRET");

    public static string? GetGitHubDispatchToken(this IConfiguration configuration) =>
        configuration.GetSetting("GitHubDispatch:Token", "GITHUB_DISPATCH_TOKEN");
}
