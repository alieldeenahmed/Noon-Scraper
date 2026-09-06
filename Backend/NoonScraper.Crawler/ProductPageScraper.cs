using System.Text.Json;
using Microsoft.Playwright;
using NoonScraper.Data;

namespace NoonScraper.Crawler;

public static class ProductPageScraper
{
    // Noon embeds a schema.org Product JSON-LD block on every detail page, with
    // clean structured data (price, discount, rating, stock, seller) - far more
    // reliable than parsing the visual DOM, which uses hashed CSS-module classes
    // and renders rating as a star-fill percentage rather than a plain number.
    public static async Task<ScrapedProduct?> ScrapeAsync(IPage page, string productUrl)
    {
        var normalizedUrl = UrlNormalizer.Normalize(productUrl);
        await page.GotoAsync(normalizedUrl);
        await page.WaitForSelectorAsync("[data-qa='div-price-now']", new PageWaitForSelectorOptions
        {
            Timeout = 20000
        });

        var scripts = await page.Locator("script[type='application/ld+json']").AllTextContentsAsync();

        foreach (var scriptText in scripts)
        {
            using var doc = TryParse(scriptText);
            if (doc is null)
            {
                continue;
            }

            var root = doc.RootElement;
            if (!root.TryGetProperty("@type", out var typeProp) || typeProp.GetString() != "Product")
            {
                continue;
            }

            return ParseProduct(root, normalizedUrl);
        }

        return null;
    }

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ScrapedProduct ParseProduct(JsonElement root, string productUrl)
    {
        var name = root.GetProperty("name").GetString()!;
        var sku = root.GetProperty("sku").GetString()!;

        var offers = root.GetProperty("offers");
        if (offers.ValueKind == JsonValueKind.Array)
        {
            offers = offers[0];
        }

        var price = offers.GetProperty("price").GetDecimal();

        decimal? discountPercent = null;
        if (offers.TryGetProperty("priceSpecification", out var priceSpec) &&
            priceSpec.TryGetProperty("price", out var wasPriceProp))
        {
            var wasPrice = wasPriceProp.GetDecimal();
            if (wasPrice > price)
            {
                discountPercent = Math.Round((wasPrice - price) / wasPrice * 100, 0);
            }
        }

        decimal? rating = null;
        if (root.TryGetProperty("aggregateRating", out var aggRating) &&
            aggRating.TryGetProperty("ratingValue", out var ratingValueProp))
        {
            rating = ratingValueProp.GetDecimal();
        }

        string? merchantName = null;
        if (offers.TryGetProperty("seller", out var seller) &&
            seller.TryGetProperty("name", out var sellerNameProp))
        {
            merchantName = sellerNameProp.GetString();
        }

        var stock = offers.TryGetProperty("availability", out var availabilityProp) &&
            availabilityProp.GetString() == "https://schema.org/InStock";

        return new ScrapedProduct
        {
            NoonProductId = sku,
            Url = productUrl,
            Name = name,
            Price = price,
            DiscountPercent = discountPercent,
            Rating = rating,
            MerchantName = merchantName,
            Stock = stock
        };
    }
}
