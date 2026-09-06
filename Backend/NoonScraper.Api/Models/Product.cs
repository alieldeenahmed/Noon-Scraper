namespace NoonScraper.Api.Models;

public class Product
{
    public int Id { get; set; }

    public required string Url { get; set; }

    public string? NoonProductId { get; set; }

    public string? Name { get; set; }

    public string? MerchantName { get; set; }

    public Category? Category { get; set; }

    public decimal? Rating { get; set; }

    public required ProductSource Source { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<PriceSnapshot> PriceSnapshots { get; set; } = [];

    public ICollection<NotificationSubscription> NotificationSubscriptions { get; set; } = [];
}
