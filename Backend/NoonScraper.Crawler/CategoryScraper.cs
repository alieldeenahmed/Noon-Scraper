using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NoonScraper.Crawler;

public static class CategoryScraper
{
    public static async Task<List<ScrapedProduct>> ScrapeAsync(IPage page, string categoryUrl)
    {
        await page.GotoAsync(categoryUrl);
        await page.WaitForSelectorAsync("[data-qa='plp-product-box']", new PageWaitForSelectorOptions
        {
            Timeout = 15000
        });

        var boxes = await page.Locator("[data-qa='plp-product-box']").AllAsync();
        var results = new List<ScrapedProduct>();

        foreach (var box in boxes)
        {
            var href = await box.Locator("a").First.GetAttributeAsync("href");
            if (href is null)
            {
                continue;
            }

            var noonProductId = ExtractNoonProductId(href);
            if (noonProductId is null)
            {
                continue;
            }

            // Noon uses two different page templates for product listings: carousel-based
            // category pages (data-qa="product-box-name/price") and search-result grid pages
            // (data-qa="plp-product-box-name/price"). Both selectors are combined so either
            // template resolves.
            var nameLocator = box.Locator("[data-qa='product-box-name'], [data-qa='plp-product-box-name']");
            var name = await nameLocator.GetAttributeAsync("title")
                ?? await nameLocator.InnerTextAsync();

            var priceBox = box.Locator("[data-qa='product-box-price'], [data-qa='plp-product-box-price']");

            var priceText = await priceBox.Locator("[class*='_amount_']").First.InnerTextAsync();
            var price = ParseDecimal(priceText);

            decimal? discountPercent = null;
            var discountLocator = priceBox.Locator("[class*='_discount_']");
            if (await discountLocator.CountAsync() > 0)
            {
                var discountText = await discountLocator.First.InnerTextAsync();
                discountPercent = TryParseDecimal(discountText);
            }

            decimal? rating = null;
            var ratingLocator = box.Locator("[class*='_textCtr_']");
            if (await ratingLocator.CountAsync() > 0)
            {
                var ratingText = await ratingLocator.First.InnerTextAsync();
                rating = TryParseDecimal(ratingText);
            }

            results.Add(new ScrapedProduct
            {
                NoonProductId = noonProductId,
                Url = new Uri(new Uri("https://www.noon.com"), href).ToString(),
                Name = name.Trim(),
                Price = price,
                DiscountPercent = discountPercent,
                Rating = rating
            });
        }

        return results;
    }

    private static string? ExtractNoonProductId(string href)
    {
        var path = href.Split('?')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pIndex = Array.LastIndexOf(segments, "p");
        return pIndex > 0 ? segments[pIndex - 1] : null;
    }

    // Noon renders badge text (discount, rating) inconsistently - sometimes with
    // surrounding Arabic text ("خصم 6%"), sometimes English ("16% OFF") - so this
    // pulls out the first numeric token rather than assuming a fixed format.
    private static decimal ParseDecimal(string text)
    {
        var match = Regex.Match(text, @"[\d,]+(\.\d+)?");
        if (!match.Success)
        {
            throw new FormatException($"No numeric value found in '{text}'");
        }

        return decimal.Parse(match.Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture);
    }

    private static decimal? TryParseDecimal(string text)
    {
        var match = Regex.Match(text, @"[\d,]+(\.\d+)?");
        if (!match.Success)
        {
            return null;
        }

        return decimal.Parse(match.Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture);
    }
}
