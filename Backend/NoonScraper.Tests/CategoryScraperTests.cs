using NoonScraper.Crawler;

namespace NoonScraper.Tests;

// Category listing pages: read straight off the tiles' data-qa attributes.
[Trait("Category", "Browser")]
public class CategoryScraperTests(BrowserFixture browser) : IClassFixture<BrowserFixture>
{
    private const string CategoryUrl = "https://www.noon.com/egypt-en/mobiles/";

    private async Task<List<ScrapedProduct>> ScrapeAsync(string html) => (await ScrapeFullAsync(html)).Products.ToList();

    private async Task<CategoryScrapeResult> ScrapeFullAsync(string html)
    {
        var page = await browser.PageServingAsync(html);
        return await CategoryScraper.ScrapeAsync(page, CategoryUrl, TimeSpan.FromSeconds(3));
    }

    private static string Tile(string name = "Some Phone", string price = "1,000", string? discount = null,
        string? rating = "4.4", string href = "/egypt-en/some-phone/N1/p/?o=abc", bool grid = false) =>
        Fixtures.Tile(href, name, price, discount, rating, grid);

    // The three tiles in category-page.html are unmodified captures of a real
    // page; the expected values below were read off that markup, not off the scraper.
    [Fact]
    public async Task Reads_real_tiles()
    {
        var products = await ScrapeAsync(Fixtures.Read("category-page.html"));

        Assert.Equal(3, products.Count);

        var discounted = products[0];
        Assert.Equal("Samsung Galaxy A07 Dual SIM Green 4GB 64GB 4G - Middle East Version", discounted.Name);
        Assert.Equal("N70202442V", discounted.NoonProductId);
        Assert.Equal(8999m, discounted.Price);
        Assert.Equal(10m, discounted.DiscountPercent);
        Assert.Equal(4.4m, discounted.Rating);

        var undiscounted = products[1];
        Assert.Equal("N70158922V", undiscounted.NoonProductId);
        Assert.Equal(20799m, undiscounted.Price);
        Assert.Null(undiscounted.DiscountPercent);
        Assert.Equal(4.4m, undiscounted.Rating);

        Assert.Equal(17175m, products[2].Price);
        Assert.Equal(11m, products[2].DiscountPercent);
    }

    [Fact]
    public async Task Strips_the_tracking_query_string_from_the_url()
    {
        var products = await ScrapeAsync(Fixtures.Read("category-page.html"));

        Assert.Equal(
            "https://www.noon.com/egypt-en/galaxy-a07-dual-sim-green-4gb-64gb-4g-middle-east-version/N70202442V/p/",
            products[0].Url);
        Assert.All(products, p => Assert.DoesNotContain("?", p.Url));
    }

    [Fact]
    public async Task Listing_tiles_carry_no_stock_signal_so_stock_defaults_to_true()
    {
        var products = await ScrapeAsync(Fixtures.Read("category-page.html"));

        Assert.All(products, p => Assert.True(p.Stock));
    }

    [Fact]
    public async Task Reads_the_search_grid_template_as_well_as_the_carousel_one()
    {
        var products = await ScrapeAsync(Fixtures.Page(
            Tile(name: "Carousel Phone", price: "2,000", grid: false)
            + Tile(name: "Grid Phone", price: "3,000", href: "/egypt-en/grid-phone/N2/p/", grid: true)));

        Assert.Equal(["Carousel Phone", "Grid Phone"], products.Select(p => p.Name));
        Assert.Equal([2000m, 3000m], products.Select(p => p.Price));
    }

    [Theory]
    [InlineData("1,299", 1299)]
    [InlineData("12,999.50", 12999.5)]
    [InlineData("99", 99)]
    public async Task Parses_prices_with_thousands_separators(string shown, double expected)
    {
        var products = await ScrapeAsync(Fixtures.Page(Tile(price: shown)));

        Assert.Equal((decimal)expected, Assert.Single(products).Price);
    }

    // Noon renders badge text inconsistently, sometimes in Arabic - the parser
    // takes the first numeric token rather than assuming a format.
    [Theory]
    [InlineData("16% OFF", 16)]
    [InlineData("خصم 6%", 6)]
    [InlineData("-25%", 25)]
    public async Task Pulls_the_discount_number_out_of_badge_text(string badge, double expected)
    {
        var products = await ScrapeAsync(Fixtures.Page(Tile(discount: badge)));

        Assert.Equal((decimal)expected, Assert.Single(products).DiscountPercent);
    }

    [Fact]
    public async Task A_badge_with_no_number_means_no_discount_rather_than_a_crash()
    {
        var products = await ScrapeAsync(Fixtures.Page(Tile(discount: "NEW")));

        Assert.Null(Assert.Single(products).DiscountPercent);
    }

    [Fact]
    public async Task A_tile_without_a_rating_still_scrapes()
    {
        var products = await ScrapeAsync(Fixtures.Page(Tile(rating: null)));

        Assert.Null(Assert.Single(products).Rating);
    }

    [Fact]
    public async Task Trims_whitespace_from_names()
    {
        var products = await ScrapeAsync(Fixtures.Page(Tile(name: "   Padded Phone   ")));

        Assert.Equal("Padded Phone", Assert.Single(products).Name);
    }

    [Fact]
    public async Task Skips_tiles_that_do_not_link_to_a_product_page()
    {
        var products = await ScrapeAsync(Fixtures.Page(
            Tile(name: "Real Product", href: "/egypt-en/real-product/N1/p/")
            + Tile(name: "Promo Banner", href: "/egypt-en/mobiles/samsung-deals/")
            + Tile(name: "Another Real One", href: "/egypt-en/another/N3/p/?o=x")));

        Assert.Equal(["Real Product", "Another Real One"], products.Select(p => p.Name));
        Assert.Equal(["N1", "N3"], products.Select(p => p.NoonProductId));
    }
}

// Failure behaviour: what the scraper reports when the page isn't what it expects.
[Trait("Category", "Browser")]
public class CategoryScraperFailureTests(BrowserFixture browser) : IClassFixture<BrowserFixture>
{
    [Fact]
    public async Task A_page_with_no_tiles_throws_so_the_crawl_can_skip_that_category()
    {
        // e.g. an anti-bot interstitial instead of the listing. The daily crawl
        // catches this per category and carries on with the next one.
        var page = await browser.PageServingAsync(Fixtures.Page("<h1>Access denied</h1>"));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            CategoryScraper.ScrapeAsync(page, "https://www.noon.com/egypt-en/mobiles/", TimeSpan.FromMilliseconds(400)));
    }

    [Theory]
    [InlineData(403, true)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(404, false)]
    [InlineData(410, false)]
    public async Task An_http_error_is_reported_with_whether_retrying_could_help(int status, bool transient)
    {
        var page = await browser.PageServingAsync("<h1>nope</h1>", status);

        var ex = await Assert.ThrowsAsync<ScrapeNavigationException>(() =>
            CategoryScraper.ScrapeAsync(page, "https://www.noon.com/egypt-en/mobiles/", TimeSpan.FromMilliseconds(300)));

        Assert.Equal(status, ex.Status);
        Assert.Equal(transient, ex.IsTransient);
    }

    // One tile with no price element used to make InnerText wait out Playwright's
    // 30-second default and then throw - losing the entire category. It is now
    // skipped immediately and reported.
    [Fact]
    public async Task A_tile_with_no_price_is_skipped_quickly_and_the_rest_are_kept()
    {
        var broken = Fixtures.Tile("/egypt-en/broken/N2/p/", "Broken", "1,000").Replace("_amount_", "_amountGone_");
        var html = Fixtures.Page(
            Fixtures.Tile("/egypt-en/good-one/N1/p/", "Good One", "1,000")
            + broken
            + Fixtures.Tile("/egypt-en/good-two/N3/p/", "Good Two", "2,000"));
        var page = await browser.PageServingAsync(html);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await CategoryScraper.ScrapeAsync(page, "https://www.noon.com/egypt-en/mobiles/", TimeSpan.FromSeconds(3));
        timer.Stop();

        Assert.Equal(["Good One", "Good Two"], result.Products.Select(p => p.Name));
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains("no price element", skipped.Reason);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
    }

    [Fact]
    public async Task A_tile_with_no_name_element_is_skipped()
    {
        var nameless = Fixtures.Tile("/egypt-en/x/N2/p/", "X", "1,000")
            .Replace("data-qa=\"product-box-name\"", "data-qa=\"something-else\"");
        var page = await browser.PageServingAsync(Fixtures.Page(nameless + Fixtures.Tile("/egypt-en/ok/N1/p/", "Ok", "5")));

        var result = await CategoryScraper.ScrapeAsync(page, "https://www.noon.com/egypt-en/mobiles/", TimeSpan.FromSeconds(3));

        Assert.Equal(["Ok"], result.Products.Select(p => p.Name));
        Assert.Contains("no name element", Assert.Single(result.Skipped).Reason);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("free")]
    public async Task A_tile_with_a_zero_or_unreadable_price_is_skipped(string price)
    {
        var page = await browser.PageServingAsync(Fixtures.Page(
            Fixtures.Tile("/egypt-en/x/N2/p/", "X", price) + Fixtures.Tile("/egypt-en/ok/N1/p/", "Ok", "5")));

        var result = await CategoryScraper.ScrapeAsync(page, "https://www.noon.com/egypt-en/mobiles/", TimeSpan.FromSeconds(3));

        Assert.Equal(["Ok"], result.Products.Select(p => p.Name));
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task Prices_written_in_arabic_indic_digits_are_read()
    {
        var page = await browser.PageServingAsync(Fixtures.Page(Fixtures.Tile("/egypt-en/x/N1/p/", "X", "١٢٬٩٩٩")));

        var result = await CategoryScraper.ScrapeAsync(page, "https://www.noon.com/egypt-en/mobiles/", TimeSpan.FromSeconds(3));

        Assert.Equal(12999m, Assert.Single(result.Products).Price);
    }
}
