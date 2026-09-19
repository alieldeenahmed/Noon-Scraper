using System.Text.Json.Nodes;
using NoonScraper.Crawler;

namespace NoonScraper.Tests;

// Cross-merchant offers. OfferScraper deliberately waits for the page to hydrate
// (network idle plus a fixed 5 seconds) before looking for the "other sellers"
// trigger, so every test here costs at least that long - hence the split into
// two classes, which xUnit runs in parallel.

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

        var offers = await OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl);

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
    public async Task Falls_back_to_the_default_offer_when_the_panel_never_renders_any_cards()
    {
        // Trigger is there, but clicking it produces no seller cards.
        var page = await browser.PageServingAsync(Fixtures.OffersPage("", Anker()));

        var offers = await OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl);

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
        return await OfferScraper.ScrapeOffersAsync(page, Fixtures.ProductUrl);
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
}
