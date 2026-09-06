using Microsoft.EntityFrameworkCore;
using NoonScraper.Data.Models;

namespace NoonScraper.Data;

// Rule-based checks run against a product's existing PriceSnapshot history each
// time a new snapshot is about to be recorded. Both checks need at least one
// prior snapshot, so neither fires for a product seen for the first time.
public static class PriceHistoryAnalyzer
{
    public static async Task<bool> IsRestockAsync(AppDbContext db, int productId, bool newStock)
    {
        if (!newStock || productId == 0)
        {
            return false;
        }

        var previousStock = await db.PriceSnapshots
            .Where(s => s.ProductId == productId)
            .OrderByDescending(s => s.CrawledAt)
            .Select(s => (bool?)s.Stock)
            .FirstOrDefaultAsync();

        return previousStock == false;
    }

    // Flags a discount as suspicious when the price recently spiked (at least
    // 10% above the lowest price ever recorded) and today's "discounted" price
    // still doesn't beat that historical low - meaning the percentage Noon
    // advertises is measured against an inflated "before" price, not a genuine
    // improvement over what the price already was before the spike.
    public static async Task<DiscountFlag?> DetectFakeDiscountAsync(
        AppDbContext db,
        Product product,
        PriceSnapshot triggeringSnapshot,
        decimal newPrice,
        decimal? newDiscountPercent)
    {
        if (newDiscountPercent is null || product.Id == 0)
        {
            return null;
        }

        var priorSnapshots = await db.PriceSnapshots
            .Where(s => s.ProductId == product.Id)
            .OrderBy(s => s.CrawledAt)
            .Select(s => new { s.Price, s.CrawledAt })
            .ToListAsync();

        if (priorSnapshots.Count == 0)
        {
            return null;
        }

        var historicalLow = priorSnapshots.Min(s => s.Price);
        var mostRecent = priorSnapshots[^1];

        const decimal SpikeThreshold = 1.1m;
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
            DiscountedPrice = newPrice,
            DiscountPercent = newDiscountPercent.Value,
            DetectedAt = DateTimeOffset.UtcNow
        };
    }
}
