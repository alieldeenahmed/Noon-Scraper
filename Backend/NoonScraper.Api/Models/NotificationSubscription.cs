namespace NoonScraper.Api.Models;

public class NotificationSubscription
{
    public int Id { get; set; }

    public required int ProductId { get; set; }

    public Product? Product { get; set; }

    public required long TelegramChatId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public decimal? LastNotifiedPrice { get; set; }

    public DateTimeOffset? LastNotifiedAt { get; set; }
}
