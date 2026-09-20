using Microsoft.Playwright;
using NoonScraper.Data;

namespace NoonScraper.Crawler;

public static class ProductPageScraper
{
    private static readonly TimeSpan DefaultHydrationTimeout = TimeSpan.FromSeconds(20);

    // Noon embeds a schema.org Product JSON-LD block on every detail page, with
    // clean structured data (price, discount, rating, stock, seller) - far more
    // reliable than parsing the visual DOM, which uses hashed CSS-module classes
    // and renders rating as a star-fill percentage rather than a plain number.
    //
    // Returns null when the page has no Product block (likely an anti-bot page);
    // throws ScrapeNavigationException for an HTTP error and ScrapeParseException
    // when the block is present but unusable.
    public static async Task<ScrapedProduct?> ScrapeAsync(
        IPage page, string productUrl, TimeSpan? hydrationTimeout = null)
    {
        var normalizedUrl = UrlNormalizer.Normalize(productUrl);
        var response = await page.GotoAsync(normalizedUrl);
        if (response is { Ok: false })
        {
            throw new ScrapeNavigationException(response.Status, normalizedUrl);
        }

        // The price element appearing means the page finished hydrating. The
        // JSON-LD is server-rendered, so a page that never shows a price (say, an
        // unavailable product) may still carry perfectly good structured data -
        // give hydration a chance, then read what's there either way.
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

        var scripts = await page.Locator("script[type='application/ld+json']").AllTextContentsAsync();
        return ProductJsonLd.ParseProduct(scripts, normalizedUrl);
    }
}
