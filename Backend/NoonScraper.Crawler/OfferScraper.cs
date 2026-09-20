using Microsoft.Playwright;

namespace NoonScraper.Crawler;

// Noon only ever embeds the currently-selected offer in JSON-LD (a single
// object, not the full list of sellers). The full list only renders after
// clicking "More offers from other sellers", which opens a panel of per-seller
// cards - if that trigger doesn't exist, the product genuinely has one seller
// and the JSON-LD offer is the whole story.
public static class OfferScraper
{
    private static readonly TimeSpan DefaultHydrationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromSeconds(5);

    // settleDelay is how long to let the page finish hydrating before looking
    // for the "other sellers" trigger, which renders well after the price does.
    // Production leaves it at the default; tests shorten it.
    public static async Task<List<OfferResult>> ScrapeOffersAsync(
        IPage page,
        string productUrl,
        TimeSpan? settleDelay = null,
        TimeSpan? hydrationTimeout = null)
    {
        var response = await page.GotoAsync(productUrl);
        if (response is { Ok: false })
        {
            throw new ScrapeNavigationException(response.Status, productUrl);
        }

        try
        {
            await page.WaitForSelectorAsync("[data-qa='div-price-now']", new PageWaitForSelectorOptions
            {
                Timeout = (float)(hydrationTimeout ?? DefaultHydrationTimeout).TotalMilliseconds
            });
        }
        catch (TimeoutException)
        {
        }

        // The "other sellers" section hydrates well after the initial price data -
        // checking for the trigger immediately after price load misses it.
        var settle = settleDelay ?? DefaultSettleDelay;
        if (settle > TimeSpan.Zero)
        {
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 });
            }
            catch (TimeoutException)
            {
                // Some background requests (analytics, tracking pixels) never go idle -
                // proceed anyway once the fixed wait below gives the page a chance to settle.
            }

            await page.WaitForTimeoutAsync((float)settle.TotalMilliseconds);
        }

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

            // A card missing its seller or price is skipped rather than failing the
            // comparison: the other sellers' offers are still worth showing.
            var sellerName = card.Locator("[class*='_sellerName_']");
            var sellingPrice = card.Locator("[class*='_sellingPrice_']");
            if (await sellerName.CountAsync() == 0 || await sellingPrice.CountAsync() == 0)
            {
                continue;
            }

            if (!PriceText.TryParse(await sellingPrice.First.InnerTextAsync(), out var price) || price <= 0)
            {
                continue;
            }

            decimal? rating = null;
            var ratingLocator = card.Locator("[class*='_textValue_']");
            if (await ratingLocator.CountAsync() > 0)
            {
                rating = PriceText.TryParseOrNull(await ratingLocator.First.InnerTextAsync());
            }

            offers.Add(new OfferResult
            {
                MerchantName = (await sellerName.First.InnerTextAsync()).Trim(),
                Price = price,
                Rating = rating
            });
        }

        return offers;
    }

    private static async Task<List<OfferResult>> ScrapeDefaultOfferAsync(IPage page)
    {
        var scripts = await page.Locator("script[type='application/ld+json']").AllTextContentsAsync();
        var offer = ProductJsonLd.ParseDefaultOffer(scripts);
        return offer is null ? [] : [offer];
    }
}
