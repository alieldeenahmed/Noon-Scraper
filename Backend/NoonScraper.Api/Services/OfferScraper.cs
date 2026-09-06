using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NoonScraper.Api.Services;

// Noon only ever embeds the currently-selected offer in JSON-LD (a single
// object, not the full list of sellers). The full list only renders after
// clicking "More offers from other sellers", which opens a panel of per-seller
// cards - if that trigger doesn't exist, the product genuinely has one seller
// and the JSON-LD offer is the whole story.
public static class OfferScraper
{
    public static async Task<List<OfferResult>> ScrapeOffersAsync(IPage page, string productUrl)
    {
        await page.GotoAsync(productUrl);
        await page.WaitForSelectorAsync("[data-qa='div-price-now']", new PageWaitForSelectorOptions
        {
            Timeout = 20000
        });

        // The "other sellers" section hydrates well after the initial price data -
        // checking for the trigger immediately after price load misses it.
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            // Some background requests (analytics, tracking pixels) never go idle -
            // proceed anyway once the fixed wait below gives the page a chance to settle.
        }
        await page.WaitForTimeoutAsync(5000);

        var trigger = page.Locator("text=More offers from other sellers");
        if (await trigger.CountAsync() == 0)
        {
            return await ScrapeDefaultOfferAsync(page);
        }

        await trigger.First.ClickAsync();

        var cards = page.Locator("a:has([class*='_sellerName_'])");
        try
        {
            await cards.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        }
        catch (TimeoutException)
        {
            return await ScrapeDefaultOfferAsync(page);
        }

        var count = await cards.CountAsync();
        var offers = new List<OfferResult>();

        for (var i = 0; i < count; i++)
        {
            var card = cards.Nth(i);

            var sellerName = await card.Locator("[class*='_sellerName_']").First.InnerTextAsync();
            var priceText = await card.Locator("[class*='_sellingPrice_']").First.InnerTextAsync();
            var price = ParseDecimal(priceText);

            decimal? rating = null;
            var ratingLocator = card.Locator("[class*='_textValue_']");
            if (await ratingLocator.CountAsync() > 0)
            {
                rating = TryParseDecimal(await ratingLocator.First.InnerTextAsync());
            }

            offers.Add(new OfferResult
            {
                MerchantName = sellerName.Trim(),
                Price = price,
                Rating = rating
            });
        }

        return offers;
    }

    private static async Task<List<OfferResult>> ScrapeDefaultOfferAsync(IPage page)
    {
        var scripts = await page.Locator("script[type='application/ld+json']").AllTextContentsAsync();

        foreach (var scriptText in scripts)
        {
            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(scriptText);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("@type", out var typeProp) || typeProp.GetString() != "Product")
                {
                    continue;
                }

                var offers = root.GetProperty("offers");
                if (offers.ValueKind == JsonValueKind.Array)
                {
                    offers = offers[0];
                }

                var price = offers.GetProperty("price").GetDecimal();

                var merchantName = "noon";
                if (offers.TryGetProperty("seller", out var seller) &&
                    seller.TryGetProperty("name", out var sellerNameProp))
                {
                    merchantName = sellerNameProp.GetString() ?? "noon";
                }

                return [new OfferResult { MerchantName = merchantName, Price = price }];
            }
        }

        return [];
    }

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
        return match.Success
            ? decimal.Parse(match.Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture)
            : null;
    }
}
