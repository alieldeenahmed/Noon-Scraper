using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NoonScraper.Api.Dtos;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

public class ProductListTests
{
    private static async Task<PagedResultDto<ProductListItemDto>> GetPage(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/products{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedResultDto<ProductListItemDto>>(Json.Options))!;
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // Five products: three crawled (with different prices/discounts/categories),
    // one out of stock, one never crawled.
    private static ApiFactory SeededFactory(Dictionary<string, string?>? settings = null)
    {
        var factory = new ApiFactory(settings);
        factory.Seed(db =>
        {
            var laptop = TestDb.AddProduct(db, "https://www.noon.com/a/N1/p/", "Lenovo ThinkPad E14", Category.Laptops);
            TestDb.AddSnapshot(db, laptop, 60000, crawledAt: T0.AddHours(3), discountPercent: 10);

            var phone = TestDb.AddProduct(db, "https://www.noon.com/b/N2/p/", "Samsung Galaxy S26", Category.Mobiles);
            TestDb.AddSnapshot(db, phone, 30000, crawledAt: T0.AddHours(2), discountPercent: 25);

            var cream = TestDb.AddProduct(db, "https://www.noon.com/c/N3/p/", "Nivea Cream", Category.SkinCare);
            TestDb.AddSnapshot(db, cream, 150, crawledAt: T0.AddHours(1));

            var soldOut = TestDb.AddProduct(db, "https://www.noon.com/d/N4/p/", "Sold Out Shampoo", Category.HairCare);
            TestDb.AddSnapshot(db, soldOut, 90, stock: false, crawledAt: T0);

            TestDb.AddProduct(db, "https://www.noon.com/egypt-en/lenovo-legion/N5/p/", name: null, source: ProductSource.UserAdded);
        });
        return factory;
    }

    [Fact]
    public async Task Returns_a_paged_envelope()
    {
        using var factory = SeededFactory();
        var client = factory.CreateClient();

        var first = await GetPage(client, "?pageSize=2");
        var last = await GetPage(client, "?pageSize=2&page=3");

        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(last.Items);
        Assert.Equal(3, last.Page);
    }

    [Fact]
    public async Task Clamps_page_and_page_size_instead_of_failing()
    {
        using var factory = SeededFactory();
        var client = factory.CreateClient();

        var page = await GetPage(client, "?page=-4&pageSize=9999");

        Assert.Equal(1, page.Page);
        Assert.Equal(100, page.PageSize);
        Assert.Equal(5, page.Items.Count);
    }

    [Fact]
    public async Task Defaults_to_most_recently_crawled_first_with_never_crawled_last()
    {
        using var factory = SeededFactory();

        var page = await GetPage(factory.CreateClient());

        Assert.Equal(
            ["Lenovo ThinkPad E14", "Samsung Galaxy S26", "Nivea Cream", "Sold Out Shampoo", null],
            page.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Sorts_by_price_in_both_directions_with_uncrawled_products_last()
    {
        using var factory = SeededFactory();
        var client = factory.CreateClient();

        var desc = await GetPage(client, "?sortBy=price&sortDir=desc");
        var asc = await GetPage(client, "?sortBy=price&sortDir=asc");

        Assert.Equal([60000m, 30000m, 150m, 90m, null], desc.Items.Select(i => i.LatestPrice));
        Assert.Equal([90m, 150m, 30000m, 60000m, null], asc.Items.Select(i => i.LatestPrice));
    }

    [Fact]
    public async Task Sorts_by_discount_with_undiscounted_products_last()
    {
        using var factory = SeededFactory();

        var page = await GetPage(factory.CreateClient(), "?sortBy=discount&sortDir=desc");

        Assert.Equal(25, page.Items[0].LatestDiscountPercent);
        Assert.Equal(10, page.Items[1].LatestDiscountPercent);
        Assert.All(page.Items.Skip(2), i => Assert.Null(i.LatestDiscountPercent));
    }

    [Fact]
    public async Task Filters_by_category()
    {
        using var factory = SeededFactory();

        var page = await GetPage(factory.CreateClient(), "?category=Mobiles");

        var item = Assert.Single(page.Items);
        Assert.Equal("Samsung Galaxy S26", item.Name);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Search_matches_names_case_insensitively_and_falls_back_to_the_url_for_uncrawled_products()
    {
        using var factory = SeededFactory();

        var page = await GetPage(factory.CreateClient(), "?search=LENOVO");

        // "Lenovo ThinkPad E14" by name, plus the uncrawled product whose URL contains "lenovo".
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task Search_and_category_combine()
    {
        using var factory = SeededFactory();

        var page = await GetPage(factory.CreateClient(), "?search=lenovo&category=Laptops");

        Assert.Equal("Lenovo ThinkPad E14", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task Search_treats_wildcard_characters_literally()
    {
        using var factory = SeededFactory();

        var page = await GetPage(factory.CreateClient(), "?search=%25");

        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Shows_the_latest_snapshot_not_an_older_one()
    {
        using var factory = new ApiFactory();
        factory.Seed(db =>
        {
            var product = TestDb.AddProduct(db);
            TestDb.AddSnapshot(db, product, 500, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 450, crawledAt: T0.AddDays(1));
        });

        var page = await GetPage(factory.CreateClient());

        Assert.Equal(450, Assert.Single(page.Items).LatestPrice);
    }

    [Theory]
    [InlineData("?sortBy=name")]
    [InlineData("?sortDir=sideways")]
    public async Task Rejects_unknown_sort_options(string query)
    {
        using var factory = SeededFactory();

        var response = await factory.CreateClient().GetAsync($"/api/products{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stats_counts_total_in_stock_and_discounted()
    {
        using var factory = SeededFactory();

        var stats = await factory.CreateClient().GetFromJsonAsync<ProductStatsDto>("/api/products/stats", Json.Options);

        Assert.NotNull(stats);
        Assert.Equal(5, stats.Total);
        // Everything but the one out-of-stock product (never-crawled counts as in stock).
        Assert.Equal(4, stats.InStock);
        Assert.Equal(2, stats.OnDiscount);
    }

    [Fact]
    public async Task Stats_can_be_scoped_to_a_category()
    {
        using var factory = SeededFactory();

        var stats = await factory.CreateClient()
            .GetFromJsonAsync<ProductStatsDto>("/api/products/stats?category=HairCare", Json.Options);

        Assert.NotNull(stats);
        Assert.Equal(1, stats.Total);
        Assert.Equal(0, stats.InStock);
    }
}

public class ProductEndpointTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static Task<HttpResponseMessage> Submit(HttpClient client, string url) =>
        client.PostAsJsonAsync("/api/products", new CreateProductRequestDto { Url = url });

    [Fact]
    public async Task Unknown_product_is_a_404()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/products/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/products/999/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/products/999/discount-flags")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/products/999/restocks")).StatusCode);
    }

    [Fact]
    public async Task History_is_oldest_first()
    {
        using var factory = new ApiFactory();
        int id = 0;
        factory.Seed(db =>
        {
            var product = TestDb.AddProduct(db);
            id = product.Id;
            TestDb.AddSnapshot(db, product, 200, crawledAt: T0.AddDays(2));
            TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            TestDb.AddSnapshot(db, product, 150, crawledAt: T0.AddDays(1));
        });

        var history = await factory.CreateClient()
            .GetFromJsonAsync<List<PriceSnapshotDto>>($"/api/products/{id}/history", Json.Options);

        Assert.Equal([100m, 150m, 200m], history!.Select(s => s.Price));
    }

    [Theory]
    [InlineData("https://example.com/egypt-en/thing/N1/p/")]
    [InlineData("https://evilnoon.com.example.org/thing/N1/p/")]
    [InlineData("https://evilnoon.com/egypt-en/thing/N1/p/")]
    [InlineData("not a url")]
    [InlineData("/egypt-en/thing/N1/p/")]
    public async Task Rejects_urls_that_are_not_noon_product_links(string url)
    {
        using var factory = new ApiFactory();

        var response = await Submit(factory.CreateClient(), url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Dispatch.CrawledProductIds);
    }

    [Fact]
    public async Task Creates_the_product_normalizes_the_url_and_dispatches_a_crawl()
    {
        using var factory = new ApiFactory();

        var response = await Submit(factory.CreateClient(), "https://www.noon.com/egypt-en/thing/N1/p/?o=abc&pcl=xyz");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<ProductDetailDto>(Json.Options))!;
        Assert.Equal("https://www.noon.com/egypt-en/thing/N1/p/", product.Url);
        Assert.Equal(ProductSource.UserAdded, product.Source);
        Assert.Null(product.LastCrawledAt);
        Assert.Equal([product.Id], factory.Dispatch.CrawledProductIds);
    }

    [Fact]
    public async Task The_same_product_resubmitted_with_a_different_tracking_token_is_a_conflict()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        await Submit(client, "https://www.noon.com/egypt-en/thing/N1/p/?o=first");
        var second = await Submit(client, "https://www.noon.com/egypt-en/thing/N1/p/?o=second");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, factory.Query(db => db.Products.Count()));
    }

    [Fact]
    public async Task A_failed_dispatch_does_not_fail_the_submission()
    {
        using var factory = new ApiFactory();
        factory.Dispatch.ShouldFail = true;

        var response = await Submit(factory.CreateClient(), "https://www.noon.com/egypt-en/thing/N1/p/");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, factory.Query(db => db.Products.Count()));
    }

    [Fact]
    public async Task Check_now_for_an_unknown_product_is_a_404()
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().PostAsync("/api/products/999/check-now", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Check_now_records_a_pending_request_and_dispatches_it()
    {
        using var factory = new ApiFactory();
        int productId = 0;
        factory.Seed(db => productId = TestDb.AddProduct(db).Id);

        var response = await factory.CreateClient().PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options))!;
        Assert.Equal("Pending", accepted.Status);
        Assert.Equal([accepted.RequestId], factory.Dispatch.CheckedRequestIds);
    }

    [Fact]
    public async Task Check_now_marks_the_request_failed_when_the_dispatch_fails()
    {
        using var factory = new ApiFactory();
        int productId = 0;
        factory.Seed(db => productId = TestDb.AddProduct(db).Id);
        factory.Dispatch.ShouldFail = true;

        var response = await factory.CreateClient().PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var request = factory.Query(db => db.CheckNowRequests.Single());
        Assert.Equal(CheckNowStatus.Failed, request.Status);
        Assert.NotNull(request.CompletedAt);
    }

    [Fact]
    public async Task A_completed_check_returns_offers_with_the_lowest_first()
    {
        using var factory = new ApiFactory();
        int productId = 0, requestId = 0;
        factory.Seed(db =>
        {
            productId = TestDb.AddProduct(db).Id;
            // Serialized the way the crawler writes it (PascalCase, cheapest first).
            var resultJson = JsonSerializer.Serialize(new[]
            {
                new { MerchantName = "Wi-Tech", Price = 250m, Rating = (decimal?)4.1m },
                new { MerchantName = "noon", Price = 300m, Rating = (decimal?)null }
            });
            var request = new CheckNowRequest
            {
                ProductId = productId,
                Status = CheckNowStatus.Completed,
                RequestedAt = T0,
                ResultJson = resultJson
            };
            db.CheckNowRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
        });

        var result = await factory.CreateClient()
            .GetFromJsonAsync<CheckNowResultDto>($"/api/products/{productId}/check-now/{requestId}", Json.Options);

        Assert.Equal("Completed", result!.Status);
        Assert.Equal(250m, result.LowestPrice);
        Assert.Equal("Wi-Tech", result.LowestPriceMerchant);
        Assert.Equal(2, result.Offers!.Count);
    }

    [Fact]
    public async Task A_pending_check_has_no_offers_yet()
    {
        using var factory = new ApiFactory();
        int productId = 0, requestId = 0;
        factory.Seed(db =>
        {
            productId = TestDb.AddProduct(db).Id;
            var request = new CheckNowRequest { ProductId = productId, Status = CheckNowStatus.Pending, RequestedAt = T0 };
            db.CheckNowRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
        });

        var result = await factory.CreateClient()
            .GetFromJsonAsync<CheckNowResultDto>($"/api/products/{productId}/check-now/{requestId}", Json.Options);

        Assert.Equal("Pending", result!.Status);
        Assert.Null(result.Offers);
        Assert.Null(result.LowestPrice);
    }

    [Fact]
    public async Task A_check_request_is_only_visible_under_its_own_product()
    {
        using var factory = new ApiFactory();
        int otherProductId = 0, requestId = 0;
        factory.Seed(db =>
        {
            var product = TestDb.AddProduct(db, url: "https://www.noon.com/a/N1/p/");
            otherProductId = TestDb.AddProduct(db, url: "https://www.noon.com/b/N2/p/").Id;
            var request = new CheckNowRequest { ProductId = product.Id, Status = CheckNowStatus.Pending, RequestedAt = T0 };
            db.CheckNowRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
        });

        var response = await factory.CreateClient().GetAsync($"/api/products/{otherProductId}/check-now/{requestId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
