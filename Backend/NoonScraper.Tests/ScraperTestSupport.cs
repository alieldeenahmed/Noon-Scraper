using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace NoonScraper.Tests;

// Runs the real scrapers in a real browser, but with the network cut off:
// every request the page makes is answered with the HTML a test supplies, so
// the scrapers' actual selectors are exercised against saved noon.com markup
// instead of the live site.
//
// Needs Chrome installed (GitHub's ubuntu runners have it). Falls back to
// Playwright's bundled Chromium if that has been installed instead.
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();

        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        }
        catch (PlaywrightException)
        {
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
    }

    public async Task<IPage> PageServingAsync(string html, int status = 200)
    {
        var context = await _browser.NewContextAsync();
        await context.RouteAsync("**/*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "text/html; charset=utf-8",
            Body = html
        }));
        return await context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
    }
}

// Builds pages out of the saved fixtures in Fixtures/. The category tiles,
// Product JSON-LD, price element, and seller cards are trimmed copies of real
// noon.com markup; the helpers below only assemble them (and swap the values
// a test wants to vary), they don't invent structure.
public static class Fixtures
{
    public const string ProductUrl =
        "https://www.noon.com/egypt-en/anker-nano-45w-usb-c-charger-block-fast-charging-compact-foldable-plug-a2692/N70294248V/p/";

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static JsonNode ReadJson(string name) => JsonNode.Parse(Read(name))!;

    public static string Page(string body) => $"<!doctype html><html><body>{body}</body></html>";

    // ---- category listing

    // A real tile with the parts tests vary swapped in. `gridTemplate` uses the
    // plp-product-box-* attribute names of Noon's search-grid pages instead of
    // the carousel pages' product-box-* ones.
    public static string Tile(
        string href,
        string name,
        string price,
        string? discountText = null,
        string? ratingText = "4.4",
        bool gridTemplate = false)
    {
        var discountBlock = discountText is null
            ? ""
            : Read("category-tile.discount-block.html").Replace(">10%<", $">{discountText}<");
        var ratingBlock = ratingText is null
            ? ""
            : Read("category-tile.rating-block.html").Replace(">4.4<", $">{ratingText}<");

        return Read("category-tile.template.html")
            .Replace("{{HREF}}", WebUtility.HtmlEncode(href))
            .Replace("{{NAME}}", WebUtility.HtmlEncode(name))
            .Replace("{{PRICE}}", price)
            .Replace("{{NAME_QA}}", gridTemplate ? "plp-product-box-name" : "product-box-name")
            .Replace("{{PRICE_QA}}", gridTemplate ? "plp-product-box-price" : "product-box-price")
            .Replace("{{DISCOUNT_BLOCK}}", discountBlock)
            .Replace("{{RATING_BLOCK}}", ratingBlock);
    }

    // ---- product detail page

    public static string ProductPage(params JsonNode[] jsonLdBlocks) =>
        ProductPageRaw(jsonLdBlocks.Select(b => b.ToJsonString()).ToArray());

    public static string ProductPageRaw(params string[] jsonLdBlocks) =>
        Page(Read("price-now.html")
            + string.Concat(jsonLdBlocks.Select(b => $"<script type=\"application/ld+json\">{b}</script>")));

    // ---- "other sellers" panel

    // The panel only exists in the DOM after the trigger is clicked (the real
    // page renders it on demand), so the fixture injects the cards on click.
    public static string OffersPage(string cardsHtml, JsonNode productJsonLd) =>
        Page(Read("price-now.html")
            + $"<script type=\"application/ld+json\">{productJsonLd.ToJsonString()}</script>"
            + Read("offers-trigger.html")
            + "<div id=\"panel\"></div>"
            + $"<script>document.querySelector('._otherOffersCard_anlvk_9').addEventListener('click', () => {{ document.getElementById('panel').innerHTML = {JsonSerializer.Serialize(cardsHtml)}; }});</script>");
}
