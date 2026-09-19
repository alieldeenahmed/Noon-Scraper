using System.Text.Json.Nodes;
using NoonScraper.Crawler;

namespace NoonScraper.Tests;

// Product detail pages: read from the schema.org Product JSON-LD block, not the DOM.
[Trait("Category", "Browser")]
public class ProductPageScraperTests(BrowserFixture browser) : IClassFixture<BrowserFixture>
{
    private static JsonNode Anker() => Fixtures.ReadJson("product-anker.jsonld.json");

    private async Task<ScrapedProduct?> ScrapeAsync(string html, string url = Fixtures.ProductUrl)
    {
        var page = await browser.PageServingAsync(html);
        return await ProductPageScraper.ScrapeAsync(page, url);
    }

    // Both products are trimmed copies of real JSON-LD; the expected values
    // were read from those files, not from the scraper.
    [Fact]
    public async Task Reads_a_real_product()
    {
        var product = await ScrapeAsync(Fixtures.ProductPage(Anker()));

        Assert.NotNull(product);
        Assert.Equal("Anker Nano 45W USB C Charger Block Fast Charging Compact Foldable Plug A2692", product.Name);
        Assert.Equal("N70294248V", product.NoonProductId);
        Assert.Equal(874m, product.Price);
        Assert.Equal(4.3m, product.Rating);
        Assert.Equal("Wi-Tech", product.MerchantName);
        Assert.True(product.Stock);
        Assert.Null(product.DiscountPercent);
    }

    [Fact]
    public async Task Reads_a_second_real_product()
    {
        var product = await ScrapeAsync(Fixtures.ProductPage(Fixtures.ReadJson("product-lenovo.jsonld.json")));

        Assert.NotNull(product);
        Assert.Equal("N70251796V", product.NoonProductId);
        Assert.Equal(51704m, product.Price);
        Assert.Equal(5m, product.Rating);
        Assert.Equal("Lap-Tech", product.MerchantName);
    }

    [Fact]
    public async Task Strips_the_tracking_query_string_from_the_url()
    {
        var product = await ScrapeAsync(Fixtures.ProductPage(Anker()), $"{Fixtures.ProductUrl}?o=abc123&pcl=xyz");

        Assert.Equal(Fixtures.ProductUrl, product!.Url);
    }

    [Fact]
    public async Task Derives_the_discount_from_the_pre_discount_price()
    {
        var json = Anker();
        json["offers"]!["priceSpecification"] = new JsonObject { ["price"] = 1000 };

        var product = await ScrapeAsync(Fixtures.ProductPage(json));

        // (1000 - 874) / 1000 = 12.6%, rounded to a whole percent.
        Assert.Equal(13m, product!.DiscountPercent);
    }

    [Theory]
    [InlineData(874)]
    [InlineData(500)]
    public async Task A_pre_discount_price_that_is_not_higher_is_not_a_discount(int listPrice)
    {
        var json = Anker();
        json["offers"]!["priceSpecification"] = new JsonObject { ["price"] = listPrice };

        var product = await ScrapeAsync(Fixtures.ProductPage(json));

        Assert.Null(product!.DiscountPercent);
    }

    [Theory]
    [InlineData("https://schema.org/OutOfStock", false)]
    [InlineData("https://schema.org/InStock", true)]
    public async Task Reads_stock_from_availability(string availability, bool expected)
    {
        var json = Anker();
        json["offers"]!["availability"] = availability;

        var product = await ScrapeAsync(Fixtures.ProductPage(json));

        Assert.Equal(expected, product!.Stock);
    }

    [Fact]
    public async Task Missing_availability_counts_as_out_of_stock()
    {
        var json = Anker();
        json["offers"]!.AsObject().Remove("availability");

        var product = await ScrapeAsync(Fixtures.ProductPage(json));

        Assert.False(product!.Stock);
    }

    [Fact]
    public async Task Uses_the_first_offer_when_offers_is_an_array()
    {
        var json = Anker();
        var offer = json["offers"]!.DeepClone();
        var second = json["offers"]!.DeepClone();
        second["price"] = 5;
        json["offers"] = new JsonArray(offer, second);

        var product = await ScrapeAsync(Fixtures.ProductPage(json));

        Assert.Equal(874m, product!.Price);
    }

    [Fact]
    public async Task A_product_with_no_rating_or_seller_still_scrapes()
    {
        var json = Anker();
        json.AsObject().Remove("aggregateRating");
        json["offers"]!.AsObject().Remove("seller");

        var product = await ScrapeAsync(Fixtures.ProductPage(json));

        Assert.NotNull(product);
        Assert.Null(product.Rating);
        Assert.Null(product.MerchantName);
        Assert.Equal(874m, product.Price);
    }

    [Fact]
    public async Task Finds_the_product_among_the_other_json_ld_blocks_on_the_page()
    {
        // The real page also carries breadcrumb/organization blocks, and a
        // block that isn't valid JSON must be skipped, not fatal.
        var product = await ScrapeAsync(Fixtures.ProductPageRaw(
            Fixtures.Read("breadcrumb.jsonld.json"),
            "{ this is not json",
            Anker().ToJsonString()));

        Assert.Equal("N70294248V", product!.NoonProductId);
    }

    [Fact]
    public async Task Returns_null_when_the_page_has_no_product_block()
    {
        var product = await ScrapeAsync(Fixtures.ProductPageRaw(Fixtures.Read("breadcrumb.jsonld.json")));

        Assert.Null(product);
    }
}
