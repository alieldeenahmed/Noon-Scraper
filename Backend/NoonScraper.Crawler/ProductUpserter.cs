using Microsoft.EntityFrameworkCore;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Crawler;

// Shared between the main crawl loop and the single-product crawl triggered
// right after a URL submission - both need the exact same upsert/restock/
// fake-discount/notify logic, just over a different set of scraped items.
public static class ProductUpserter
{
    public static async Task<Product> UpsertAsync(
        AppDbContext db,
        HttpClient httpClient,
        string? telegramBotToken,
        Dictionary<string, Product> productsInThisRun,
        ScrapedProduct item,
        ProductSource source,
        Category? category)
    {
        if (!productsInThisRun.TryGetValue(item.Url, out var product))
        {
            product = await db.Products.FirstOrDefaultAsync(p => p.Url == item.Url);
            if (product is null)
            {
                product = new Product
                {
                    Url = item.Url,
                    Source = source,
                    IsActive = true
                };
                db.Products.Add(product);
            }

            productsInThisRun[item.Url] = product;
        }

        product.NoonProductId = item.NoonProductId;
        product.Name = item.Name;
        product.Rating = item.Rating;
        if (category is not null)
        {
            product.Category = category;
        }
        if (item.MerchantName is not null)
        {
            product.MerchantName = item.MerchantName;
        }

        var isRestock = await PriceHistoryAnalyzer.IsRestockAsync(db, product.Id, item.Stock);

        var newSnapshot = new PriceSnapshot
        {
            ProductId = product.Id,
            Product = product,
            Price = item.Price,
            Stock = item.Stock,
            DiscountPercent = item.DiscountPercent
        };
        db.PriceSnapshots.Add(newSnapshot);

        if (isRestock)
        {
            Console.WriteLine($"  RESTOCK: {product.Name ?? product.Url}");
            db.RestockEvents.Add(new RestockEvent
            {
                ProductId = product.Id,
                Product = product,
                TriggeringSnapshotId = newSnapshot.Id,
                TriggeringSnapshot = newSnapshot
            });
        }

        var fakeDiscount = await PriceHistoryAnalyzer.DetectFakeDiscountAsync(
            db, product, newSnapshot, item.Price, item.DiscountPercent);
        if (fakeDiscount is not null)
        {
            db.DiscountFlags.Add(fakeDiscount);
            Console.WriteLine(
                $"  SUSPICIOUS DISCOUNT: {product.Name ?? product.Url} - claims {item.DiscountPercent}% off, " +
                $"but {fakeDiscount.DiscountedPrice} doesn't beat the historical low");
        }

        // A brand-new product has no NotificationSubscriptions rows yet (nobody
        // could have subscribed to an id that didn't exist before this upsert),
        // so this is a no-op for it - only matters for products already tracked.
        await TelegramNotifier.NotifySubscribersAsync(db, httpClient, telegramBotToken, product, isRestock, item.Price);

        return product;
    }
}
