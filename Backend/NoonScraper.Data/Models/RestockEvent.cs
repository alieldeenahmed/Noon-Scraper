namespace NoonScraper.Data.Models;

public class RestockEvent
{
    public int Id { get; set; }

    public required int ProductId { get; set; }

    public Product? Product { get; set; }

    public required int TriggeringSnapshotId { get; set; }

    public PriceSnapshot? TriggeringSnapshot { get; set; }

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}
