namespace NoonScraper.Api.Models;

public class DiscountFlag
{
    public int Id { get; set; }

    public required int ProductId { get; set; }

    public Product? Product { get; set; }

    public required int TriggeringSnapshotId { get; set; }

    public PriceSnapshot? TriggeringSnapshot { get; set; }

    public required decimal PriorHighPrice { get; set; }

    public required DateTimeOffset PriorHighDetectedAt { get; set; }

    public required decimal DiscountedPrice { get; set; }

    public required decimal DiscountPercent { get; set; }

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}
