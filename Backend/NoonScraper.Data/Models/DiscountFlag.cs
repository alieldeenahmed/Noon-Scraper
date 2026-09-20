namespace NoonScraper.Data.Models;

public class DiscountFlag
{
    public int Id { get; set; }

    public required int ProductId { get; set; }

    public Product? Product { get; set; }

    public required int TriggeringSnapshotId { get; set; }

    public PriceSnapshot? TriggeringSnapshot { get; set; }

    public required decimal PriorHighPrice { get; set; }

    public required DateTimeOffset PriorHighDetectedAt { get; set; }

    // The lowest price seen in the lookback window when this was flagged - the
    // number the discounted price failed to beat. Null on rows from before this
    // column existed.
    public decimal? HistoricalLowPrice { get; set; }

    public required decimal DiscountedPrice { get; set; }

    public required decimal DiscountPercent { get; set; }

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}
