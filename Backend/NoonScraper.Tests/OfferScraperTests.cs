using System.Text.Json.Nodes;
using NoonScraper.Crawler;

namespace NoonScraper.Tests;

// Cross-merchant offers. In production OfferScraper waits for the page to hydrate
// (network idle plus 5 seconds) before looking for the "other sellers" trigger;
// these tests pass a zero settle delay, since the fixture page is complete on load.

// Products with a panel of competing sellers.
[Trait("Category", "Browser")]
public class OfferScraperPanelTests(BrowserFixture browser) : IClassFixture<BrowserFixture>
{
    private static JsonNode Anker() => Fixtures.ReadJson("product-anker.jsonld.json");

    // The six cards in offer-cards.html are real seller cards captured from a
    // product with a dozen sellers. Expected values were read off that markup:
    // includes the "Selected" card, a seller with a discount badge (the price
    // to read is the selling price, not the struck-through one), a seller with
    // "No ratings yet.", and a price with decimals.
    [Fact]
    public async Task Reads_every_seller_card_once_the_panel_is_opened()
    {
        var page = await browser.PageServingAsync(Fixtures.OffersPage(Fixtures.Read("offer-cards.html"), Anker()));

        var offers = await OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        Assert.Equal(
            [
                ("Kanawat for Trading and Distribution", 9699m, (decimal?)5.0m),
                ("iQ", 9789m, 4.5m),
                ("Mobiii Market", 9799m, 3.6m),
                ("Mobuy Egypt", 9897m, null),
                ("Dr.Mobile", 10301.95m, 4.7m),
                ("noon", 10999m, 4.2m)
            ],
            offers.Select(o => (o.MerchantName, o.Price, o.Rating)));
    }

    [Fact]
    public async Task A_card_missing_its_seller_or_with_an_unreadable_price_is_skipped_not_fatal()
    {
        var cards = Fixtures.Read("offer-cards.html");
        var noSeller = "<a href=\"/x\"><div class=\"_sellingPrice_x\"><strong>500</strong></div></a>";
        var badPrice = "<a href=\"/y\"><div class=\"_sellerName_x\">Ghost Seller</div><div class=\"_sellingPrice_x\"><strong>call us</strong></div></a>";
        var page = await browser.PageServingAsync(Fixtures.OffersPage(noSeller + badPrice + cards, Anker()));

        var offers = await OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        // The noSeller card doesn't match the card selector at all; the bad-price
        // card matches but is dropped. All six real ones survive.
        Assert.Equal(6, offers.Count);
        Assert.DoesNotContain(offers, o => o.MerchantName == "Ghost Seller");
    }

    [Fact]
    public async Task Falls_back_to_the_default_offer_when_the_panel_never_renders_any_cards()
    {
        // Trigger is there, but clicking it produces no seller cards.
        var page = await browser.PageServingAsync(Fixtures.OffersPage("", Anker()));

        var offers = await OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        var offer = Assert.Single(offers);
        Assert.Equal("Wi-Tech", offer.MerchantName);
        Assert.Equal(874m, offer.Price);
    }
}

// Products with no panel: the page's own JSON-LD offer is the whole story.
[Trait("Category", "Browser")]
public class OfferScraperFallbackTests(BrowserFixture browser) : IClassFixture<BrowserFixture>
{
    private static JsonNode Anker() => Fixtures.ReadJson("product-anker.jsonld.json");

    private async Task<List<OfferResult>> ScrapeAsync(string html)
    {
        var page = await browser.PageServingAsync(html);
        return await OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_single_seller_product_returns_its_one_offer()
    {
        var offers = await ScrapeAsync(Fixtures.ProductPage(Anker()));

        var offer = Assert.Single(offers);
        Assert.Equal("Wi-Tech", offer.MerchantName);
        Assert.Equal(874m, offer.Price);
        Assert.Null(offer.Rating);
    }

    [Fact]
    public async Task An_offer_with_no_seller_is_attributed_to_noon()
    {
        var json = Anker();
        json["offers"]!.AsObject().Remove("seller");

        var offer = Assert.Single(await ScrapeAsync(Fixtures.ProductPage(json)));

        Assert.Equal("noon", offer.MerchantName);
    }

    [Fact]
    public async Task Uses_the_first_offer_when_offers_is_an_array()
    {
        var json = Anker();
        var first = json["offers"]!.DeepClone();
        var second = json["offers"]!.DeepClone();
        second["price"] = 5;
        json["offers"] = new JsonArray(first, second);

        var offer = Assert.Single(await ScrapeAsync(Fixtures.ProductPage(json)));

        Assert.Equal(874m, offer.Price);
    }

    [Fact]
    public async Task Returns_nothing_when_the_page_has_no_product_data_at_all()
    {
        var offers = await ScrapeAsync(Fixtures.ProductPageRaw(Fixtures.Read("breadcrumb.jsonld.json")));

        Assert.Empty(offers);
    }

    [Fact]
    public async Task A_page_that_never_hydrates_its_price_still_yields_the_structured_offer()
    {
        // No price element to wait for (an unavailable product, say) - the wait
        // times out and the scraper reads what the JSON-LD says regardless.
        var html = Fixtures.Page($"<script type=\"application/ld+json\">{Anker().ToJsonString()}</script>");

        var offer = Assert.Single(await ScrapeAsync(html));

        Assert.Equal(874m, offer.Price);
    }

    [Fact]
    public async Task A_price_of_zero_in_the_structured_data_is_an_error_not_an_offer()
    {
        var json = Anker();
        json["offers"]!["price"] = 0;

        await Assert.ThrowsAsync<ScrapeParseException>(() => ScrapeAsync(Fixtures.ProductPage(json)));
    }

    [Fact]
    public async Task An_http_error_page_is_reported_as_such()
    {
        var page = await browser.PageServingAsync("<h1>gone</h1>", 404);

        var ex = await Assert.ThrowsAsync<ScrapeNavigationException>(() =>
            OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        Assert.Equal(404, ex.Status);
        Assert.False(ex.IsTransient);
    }
}
