using Microsoft.EntityFrameworkCore;
using NoonScraper.Data.Models;

namespace NoonScraper.Data;

// Rule-based checks run against a product's existing PriceSnapshot history each
// time a new snapshot is about to be recorded. Both need at least one prior
// snapshot, so neither fires for a product seen for the first time.
//
// Deliberately plain and explainable: every result can be reconstructed from the
// stored snapshots by hand, and the flag row records the numbers it was based on.
// Ordering is always (CrawledAt, Id), so two snapshots sharing a timestamp still
// have one defined "latest" instead of whichever row the database returns first.
public static class PriceHistoryAnalyzer
{
    // How far back a "historical low" is remembered. Without a bound, a price
    // from two years ago would make every later price look inflated, forever.
    public const int DefaultLookbackDays = 90;

    // The previous price must have been at least this multiple of the historical
    // low for it to count as a spike.
    public const decimal SpikeThreshold = 1.1m;

    // True when this crawl finds the product in stock and the most recent prior
    // snapshot had it out of stock. Only the most recent snapshot matters: a
    // product that was out of stock last month and has been in stock since
    // isn't restocking today.
    public static async Task<bool> IsRestockAsync(
        AppDbContext db, int productId, bool newStock, CancellationToken ct = default)
    {
        if (!newStock || productId == 0)
        {
            return false;
        }

        var previousStock = await db.PriceSnapshots
            .Where(s => s.ProductId == productId)
            .OrderByDescending(s => s.CrawledAt)
            .ThenByDescending(s => s.Id)
            .Select(s => (bool?)s.Stock)
            .FirstOrDefaultAsync(ct);

        return previousStock == false;
    }

    // Flags a discount as suspicious when the price recently spiked (the most
    // recent prior price was at least 10% above the lowest price in the lookback
    // window) and today's "discounted" price still doesn't beat that low - meaning
    // the percentage Noon advertises is measured against an inflated "before"
    // price, not against a price this product has really had recently.
    //
    // Only snapshots inside the lookback window count, for both the low and the
    // "most recent" comparison; a product last crawled months ago has no recent
    // history to judge a discount against, so it isn't flagged.
    public static async Task<DiscountFlag?> DetectFakeDiscountAsync(
        AppDbContext db,
        Product product,
        PriceSnapshot triggeringSnapshot,
        decimal newPrice,
        decimal? newDiscountPercent,
        DateTimeOffset now,
        CancellationToken ct = default,
        int lookbackDays = DefaultLookbackDays)
    {
        if (newDiscountPercent is null || product.Id == 0)
        {
            return null;
        }

        var cutoff = now.AddDays(-lookbackDays);
        var window = db.PriceSnapshots.Where(s => s.ProductId == product.Id && s.CrawledAt >= cutoff);

        var mostRecent = await window
            .OrderByDescending(s => s.CrawledAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Price, s.CrawledAt })
            .FirstOrDefaultAsync(ct);

        if (mostRecent is null)
        {
            return null;
        }

        // Aggregated in the database rather than loading the whole history.
        var historicalLow = await window.MinAsync(s => s.Price, ct);

        var wasRecentSpike = mostRecent.Price >= historicalLow * SpikeThreshold;
        if (!wasRecentSpike)
        {
            return null;
        }

        if (newPrice < historicalLow)
        {
            return null;
        }

        return new DiscountFlag
        {
            ProductId = product.Id,
            Product = product,
            TriggeringSnapshotId = triggeringSnapshot.Id,
            TriggeringSnapshot = triggeringSnapshot,
            PriorHighPrice = mostRecent.Price,
            PriorHighDetectedAt = mostRecent.CrawledAt,
            HistoricalLowPrice = historicalLow,
            DiscountedPrice = newPrice,
            DiscountPercent = newDiscountPercent.Value,
            DetectedAt = now
        };
    }
}
