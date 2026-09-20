using Microsoft.Playwright;
using NoonScraper.Data;

namespace NoonScraper.Crawler;

public sealed record SkippedTile(string? Href, string Reason);

// What one category page yielded: the products it could read, and the tiles it
// couldn't (with why). A single odd tile - an ad slot, a tile with no price - is
// skipped and reported rather than allowed to sink the whole category.
public sealed record CategoryScrapeResult(IReadOnlyList<ScrapedProduct> Products, IReadOnlyList<SkippedTile> Skipped);

public static class CategoryScraper
{
    private static readonly TimeSpan DefaultListingTimeout = TimeSpan.FromSeconds(15);

    // Throws (a TimeoutException) if no product tiles ever appear - an anti-bot
    // page or a changed layout - so the caller can treat the category as failed.
    public static async Task<CategoryScrapeResult> ScrapeAsync(
        IPage page, string categoryUrl, TimeSpan? listingTimeout = null)
    {
        var response = await page.GotoAsync(categoryUrl);
        if (response is { Ok: false })
        {
            throw new ScrapeNavigationException(response.Status, categoryUrl);
        }

        await page.WaitForSelectorAsync("[data-qa='plp-product-box']", new PageWaitForSelectorOptions
        {
            Timeout = (float)(listingTimeout ?? DefaultListingTimeout).TotalMilliseconds
        });

        var boxes = await page.Locator("[data-qa='plp-product-box']").AllAsync();
        var products = new List<ScrapedProduct>();
        var skipped = new List<SkippedTile>();

        foreach (var box in boxes)
        {
            string? href = null;
            try
            {
                href = await box.Locator("a").First.GetAttributeAsync("href");
                var product = await ReadTileAsync(box, href);
                if (product is null)
                {
                    skipped.Add(new SkippedTile(href, "not a product link"));
                    continue;
                }

                products.Add(product);
            }
            catch (Exception ex) when (ex is ScrapeParseException or PlaywrightException or TimeoutException)
            {
                skipped.Add(new SkippedTile(href, ex.Message.Split('\n')[0]));
            }
        }

        return new CategoryScrapeResult(products, skipped);
    }

    private static async Task<ScrapedProduct?> ReadTileAsync(ILocator box, string? href)
    {
        var noonProductId = href is null ? null : NoonUrl.ExtractSku(href);
        if (href is null || noonProductId is null)
        {
            return null;
        }

        // Noon uses two different page templates for product listings: carousel-based
        // category pages (data-qa="product-box-name/price") and search-result grid pages
        // (data-qa="plp-product-box-name/price"). Both selectors are combined so either
        // template resolves.
        var nameLocator = box.Locator("[data-qa='product-box-name'], [data-qa='plp-product-box-name']");
        var priceBox = box.Locator("[data-qa='product-box-price'], [data-qa='plp-product-box-price']");
        var amountLocator = priceBox.Locator("[class*='_amount_']");

        // CountAsync answers immediately; InnerText on a missing element would wait
        // out Playwright's 30-second default before failing.
        if (await nameLocator.CountAsync() == 0)
        {
            throw new ScrapeParseException("tile has no name element");
        }

        if (await amountLocator.CountAsync() == 0)
        {
            throw new ScrapeParseException("tile has no price element");
        }

        var name = (await nameLocator.GetAttributeAsync("title") ?? await nameLocator.InnerTextAsync()).Trim();
        if (name.Length == 0)
        {
            throw new ScrapeParseException("tile has an empty name");
        }

        var price = PriceText.Parse(await amountLocator.First.InnerTextAsync());
        if (price <= 0)
        {
            throw new ScrapeParseException("tile has a non-positive price");
        }

        decimal? discountPercent = null;
        var discountLocator = priceBox.Locator("[class*='_discount_']");
        if (await discountLocator.CountAsync() > 0)
        {
            discountPercent = PriceText.TryParseOrNull(await discountLocator.First.InnerTextAsync());
        }

        decimal? rating = null;
        var ratingLocator = box.Locator("[class*='_textCtr_']");
        if (await ratingLocator.CountAsync() > 0)
        {
            rating = PriceText.TryParseOrNull(await ratingLocator.First.InnerTextAsync());
        }

        return new ScrapedProduct
        {
            NoonProductId = noonProductId,
            Url = UrlNormalizer.Normalize(new Uri(new Uri("https://www.noon.com"), href).ToString()),
            Name = name,
            Price = price,
            DiscountPercent = discountPercent,
            Rating = rating
        };
    }
}
