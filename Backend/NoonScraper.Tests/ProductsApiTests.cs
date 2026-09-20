using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

        var page = await GetPage(factory.CreateClient(), "?page=-4&pageSize=9999");

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
        var client = factory.CreateClient();

        Assert.Equal(0, (await GetPage(client, "?search=%25")).Total);
        Assert.Equal(0, (await GetPage(client, "?search=_")).Total);
    }

    [Fact]
    public async Task An_overlong_search_is_bounded_rather_than_rejected()
    {
        using var factory = SeededFactory();

        var page = await GetPage(factory.CreateClient(), $"?search={new string('a', 5000)}");

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

    [Fact]
    public async Task Paging_is_stable_when_many_products_share_a_sort_value()
    {
        using var factory = new ApiFactory();
        factory.Seed(db =>
        {
            for (var i = 1; i <= 12; i++)
            {
                // Identical price and crawl time: only the id tie-break orders them.
                var product = TestDb.AddProduct(db, $"https://www.noon.com/egypt-en/p{i}/N{i}/p/", $"P{i}");
                TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            }
        });
        var client = factory.CreateClient();

        var seen = new List<int>();
        for (var page = 1; page <= 4; page++)
        {
            seen.AddRange((await GetPage(client, $"?sortBy=price&pageSize=3&page={page}")).Items.Select(i => i.Id));
        }

        Assert.Equal(12, seen.Distinct().Count());
    }

    [Fact]
    public async Task Untracked_products_are_hidden_from_the_list_and_the_counts()
    {
        using var factory = new ApiFactory();
        factory.Seed(db =>
        {
            TestDb.AddProduct(db, "https://www.noon.com/egypt-en/a/N1/p/", "Tracked");
            TestDb.AddProduct(db, "https://www.noon.com/egypt-en/b/N2/p/", "Abandoned", isActive: false);
        });
        var client = factory.CreateClient();

        var page = await GetPage(client);
        var stats = await client.GetFromJsonAsync<ProductStatsDto>("/api/products/stats", Json.Options);

        Assert.Equal("Tracked", Assert.Single(page.Items).Name);
        Assert.Equal(1, stats!.Total);
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
    public async Task Rejects_an_unknown_category()
    {
        using var factory = SeededFactory();

        var response = await factory.CreateClient().GetAsync("/api/products?category=Furniture");

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

    // The list joins each product to its latest snapshot in SQL. This checks it
    // does so in a fixed number of queries - one for the count, one for the page -
    // however many products and snapshots exist (no N+1).
    [Theory]
    [InlineData(5, 1)]
    [InlineData(60, 6)]
    public async Task The_list_runs_a_fixed_number_of_queries_however_much_data_there_is(int products, int snapshotsEach)
    {
        using var factory = new ApiFactory();
        factory.Seed(db =>
        {
            for (var i = 1; i <= products; i++)
            {
                var product = TestDb.AddProduct(db, $"https://www.noon.com/egypt-en/p{i}/N{i}/p/", $"P{i}");
                for (var s = 0; s < snapshotsEach; s++)
                {
                    TestDb.AddSnapshot(db, product, 100 + s, crawledAt: T0.AddHours(s));
                }
            }
        });
        var client = factory.CreateClient();
        await GetPage(client);
        factory.Commands.Reset();

        await GetPage(client, "?pageSize=25&sortBy=price&search=p");

        Assert.Equal(2, factory.Commands.Count);
    }

    [Fact]
    public async Task Stats_run_a_fixed_number_of_queries()
    {
        using var factory = SeededFactory();
        var client = factory.CreateClient();
        await client.GetAsync("/api/products/stats");
        factory.Commands.Reset();

        await client.GetAsync("/api/products/stats");

        Assert.Equal(3, factory.Commands.Count);
    }
}

public class ProductEndpointTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

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

    [Theory]
    [InlineData("/api/products/0")]
    [InlineData("/api/products/-5")]
    [InlineData("/api/products/abc")]
    [InlineData("/api/products/99999999999999999999")]
    [InlineData("/api/products/1.5")]
    public async Task Malformed_ids_are_a_404_not_an_error(string path)
    {
        using var factory = new ApiFactory();

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task History_is_oldest_first_with_ties_in_insertion_order()
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
            TestDb.AddSnapshot(db, product, 151, crawledAt: T0.AddDays(1));
        });

        var history = await factory.CreateClient()
            .GetFromJsonAsync<List<PriceSnapshotDto>>($"/api/products/{id}/history", Json.Options);

        Assert.Equal([100m, 150m, 151m, 200m], history!.Select(s => s.Price));
    }

    [Fact]
    public async Task Discount_flags_explain_themselves_and_come_newest_first()
    {
        using var factory = new ApiFactory();
        int id = 0;
        factory.Seed(db =>
        {
            var product = TestDb.AddProduct(db);
            id = product.Id;
            var older = TestDb.AddSnapshot(db, product, 140, crawledAt: T0);
            var newer = TestDb.AddSnapshot(db, product, 130, crawledAt: T0.AddDays(1));
            db.DiscountFlags.Add(new DiscountFlag
            {
                ProductId = product.Id, TriggeringSnapshotId = older.Id, PriorHighPrice = 150, PriorHighDetectedAt = T0,
                HistoricalLowPrice = 100, DiscountedPrice = 140, DiscountPercent = 30, DetectedAt = T0
            });
            db.DiscountFlags.Add(new DiscountFlag
            {
                ProductId = product.Id, TriggeringSnapshotId = newer.Id, PriorHighPrice = 140, PriorHighDetectedAt = T0,
                HistoricalLowPrice = 100, DiscountedPrice = 130, DiscountPercent = 20, DetectedAt = T0.AddDays(1)
            });
            db.SaveChanges();
        });

        var flags = await factory.CreateClient()
            .GetFromJsonAsync<List<DiscountFlagDto>>($"/api/products/{id}/discount-flags", Json.Options);

        Assert.Equal([130m, 140m], flags!.Select(f => f.DiscountedPrice));
        Assert.All(flags!, f => Assert.Equal(100m, f.HistoricalLowPrice));
    }

    [Fact]
    public async Task Restocks_come_newest_first()
    {
        using var factory = new ApiFactory();
        int id = 0;
        factory.Seed(db =>
        {
            var product = TestDb.AddProduct(db);
            id = product.Id;
            var a = TestDb.AddSnapshot(db, product, 100, crawledAt: T0);
            var b = TestDb.AddSnapshot(db, product, 100, crawledAt: T0.AddDays(3));
            db.RestockEvents.Add(new RestockEvent { ProductId = product.Id, TriggeringSnapshotId = a.Id, DetectedAt = T0 });
            db.RestockEvents.Add(new RestockEvent { ProductId = product.Id, TriggeringSnapshotId = b.Id, DetectedAt = T0.AddDays(3) });
            db.SaveChanges();
        });

        var restocks = await factory.CreateClient()
            .GetFromJsonAsync<List<RestockEventDto>>($"/api/products/{id}/restocks", Json.Options);

        Assert.Equal([T0.AddDays(3), T0], restocks!.Select(r => r.DetectedAt));
    }
}

public class CreateProductTests
{
    private const string Url = "https://www.noon.com/egypt-en/thing/N1/p/";

    private static Task<HttpResponseMessage> Submit(HttpClient client, string url) =>
        client.PostAsJsonAsync("/api/products", new CreateProductRequestDto { Url = url });

    private static async Task<ProductDetailDto> Created(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(Json.Options))!;
    }

    // Wide coverage of the validator lives in NoonUrlTests; this checks the
    // endpoint actually uses it and that nothing is stored or dispatched.
    [Theory]
    [InlineData("https://example.com/egypt-en/thing/N1/p/")]
    [InlineData("https://evilnoon.com/egypt-en/thing/N1/p/")]
    [InlineData("https://noon.com.evil.com/egypt-en/thing/N1/p/")]
    [InlineData("http://www.noon.com/egypt-en/thing/N1/p/")]
    [InlineData("https://user:pw@www.noon.com/egypt-en/thing/N1/p/")]
    [InlineData("https://www.noon.com:8443/egypt-en/thing/N1/p/")]
    [InlineData("https://www.noon.com/egypt-en/mobiles/")]
    [InlineData("https://www.noon.com/")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Rejects_anything_that_is_not_a_noon_product_link(string url)
    {
        using var factory = new ApiFactory();

        var response = await Submit(factory.CreateClient(), url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Dispatch.Dispatched);
        Assert.Equal(0, factory.Query(db => db.Products.Count()));
    }

    [Fact]
    public async Task A_missing_or_malformed_body_is_a_400()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/products", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/products", new StringContent("nope", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/products", new StringContent(Url))).StatusCode);
    }

    [Fact]
    public async Task The_error_says_what_was_wrong_without_leaking_anything()
    {
        using var factory = new ApiFactory();

        var response = await Submit(factory.CreateClient(), "https://evilnoon.com/egypt-en/thing/N1/p/");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal("Invalid product URL", problem!.Title);
        Assert.Contains("noon.com", problem.Detail);
    }

    [Fact]
    public async Task Creates_the_product_from_the_canonical_url_and_dispatches_a_crawl()
    {
        using var factory = new ApiFactory();

        var product = await Created(await Submit(
            factory.CreateClient(), "https://noon.com/egypt-en/thing/N1/p?o=abc&pcl=xyz#reviews"));

        Assert.Equal(Url, product.Url);
        Assert.Equal("N1", product.NoonProductId);
        Assert.Equal(ProductSource.UserAdded, product.Source);
        Assert.Null(product.LastCrawledAt);

        var crawl = product.Crawl!;
        Assert.Equal("Pending", crawl.Status);
        var dispatched = Assert.Single(factory.Dispatch.Dispatched);
        Assert.Equal(("crawl-product", crawl.RequestId, product.Id), (dispatched.Kind, dispatched.RequestId, dispatched.ProductId));
    }

    [Fact]
    public async Task The_dispatch_carries_the_correlation_id_stored_on_the_request()
    {
        using var factory = new ApiFactory();

        var product = await Created(await Submit(factory.CreateClient(), Url));

        var stored = factory.Query(db => db.ProductCrawlRequests.Single(r => r.ProductId == product.Id));
        Assert.NotEqual(Guid.Empty, stored.CorrelationId);
        Assert.Equal(stored.CorrelationId, Assert.Single(factory.Dispatch.Dispatched).CorrelationId);
    }

    [Theory]
    [InlineData("https://www.noon.com/egypt-en/thing/N1/p/?o=other")]
    [InlineData("https://noon.com/egypt-en/thing/N1/p")]
    [InlineData("https://WWW.NOON.COM/egypt-en/thing/N1/p/")]
    [InlineData("https://www.noon.com/egypt-en/completely-different-slug/N1/p/")]
    [InlineData("https://www.noon.com/egypt-ar/thing/N1/p/")]
    public async Task The_same_product_under_another_spelling_is_a_conflict(string variant)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var first = await Created(await Submit(client, Url));

        var second = await Submit(client, variant);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(first.Id, ((JsonElement)problem!.Extensions["productId"]!).GetInt32());
        Assert.Equal(1, factory.Query(db => db.Products.Count()));
        Assert.Single(factory.Dispatch.Dispatched);
    }

    [Fact]
    public async Task The_same_code_in_another_market_is_a_different_product()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await Created(await Submit(client, Url));

        await Created(await Submit(client, "https://www.noon.com/uae-en/thing/N1/p/"));

        Assert.Equal(2, factory.Query(db => db.Products.Count()));
    }

    // Two people submitting the same link at the same instant. Both get past the
    // "already tracked?" check; the unique index decides. Exactly one product, one
    // crawl request, one dispatch - and no 500s.
    [Fact]
    public async Task Concurrent_submissions_of_one_product_create_it_once()
    {
        using var factory = new ApiFactory(new() { ["RateLimiting:DispatchPerMinute"] = "1000", ["RateLimiting:DispatchTotalPerMinute"] = "1000" });
        var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Submit(client, Url))));

        var statuses = string.Join(", ", responses.Select(r => (int)r.StatusCode));
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.Created) == 1, $"Expected exactly one 201; got: {statuses}");
        Assert.Equal(11, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.Equal(1, factory.Query(db => db.Products.Count()));
        Assert.Equal(1, factory.Query(db => db.ProductCrawlRequests.Count()));
        Assert.Single(factory.Dispatch.Dispatched);
    }

    [Fact]
    public async Task A_failed_dispatch_still_saves_the_product_and_says_what_went_wrong()
    {
        using var factory = new ApiFactory();
        factory.Dispatch.ShouldFail = true;

        var product = await Created(await Submit(factory.CreateClient(), Url));

        Assert.Equal(1, factory.Query(db => db.Products.Count()));
        Assert.Equal("Failed", product.Crawl!.Status);
        Assert.Equal("dispatch", product.Crawl.FailureStage);
        Assert.Contains("HTTP 503", product.Crawl.ErrorMessage);
    }

    [Fact]
    public async Task An_unexpected_dispatch_failure_never_leaks_its_message()
    {
        using var factory = new ApiFactory();
        factory.Dispatch.FailureToThrow = new InvalidOperationException("token ghp_SECRET123 rejected by 10.0.0.5");

        var product = await Created(await Submit(factory.CreateClient(), Url));

        Assert.DoesNotContain("ghp_SECRET123", product.Crawl!.ErrorMessage);
        Assert.DoesNotContain("10.0.0.5", product.Crawl.ErrorMessage);
        Assert.Equal("dispatch", product.Crawl.FailureStage);
    }

    [Fact]
    public async Task Over_the_dispatch_budget_the_product_is_saved_and_the_daily_crawl_is_the_fallback()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxDispatchesPerHour"] = "0" });

        var product = await Created(await Submit(factory.CreateClient(), Url));

        Assert.Equal(1, factory.Query(db => db.Products.Count()));
        Assert.Empty(factory.Dispatch.Dispatched);
        Assert.Equal("Failed", product.Crawl!.Status);
        Assert.Equal("budget", product.Crawl.FailureStage);
        Assert.Contains("daily crawl", product.Crawl.ErrorMessage);
    }

    [Fact]
    public async Task Too_many_new_products_in_an_hour_is_a_429_with_retry_after()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxNewProductsPerHour"] = "2", ["RateLimiting:DispatchPerMinute"] = "100" });
        var client = factory.CreateClient();
        await Created(await Submit(client, "https://www.noon.com/egypt-en/a/N1/p/"));
        await Created(await Submit(client, "https://www.noon.com/egypt-en/b/N2/p/"));

        var third = await Submit(client, "https://www.noon.com/egypt-en/c/N3/p/");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal(2, factory.Query(db => db.Products.Count()));
    }

    [Fact]
    public async Task The_hourly_product_limit_frees_up_as_time_passes()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxNewProductsPerHour"] = "1", ["RateLimiting:DispatchPerMinute"] = "100" });
        var client = factory.CreateClient();
        await Created(await Submit(client, "https://www.noon.com/egypt-en/a/N1/p/"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Submit(client, "https://www.noon.com/egypt-en/b/N2/p/")).StatusCode);

        // Products carry the real clock's AddedAt, so move the fake clock past it.
        factory.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(HttpStatusCode.Created, (await Submit(client, "https://www.noon.com/egypt-en/b/N2/p/")).StatusCode);
    }

    [Fact]
    public async Task The_total_cap_on_user_products_is_enforced()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxUserProducts"] = "1", ["Jobs:MaxNewProductsPerHour"] = "100" });
        factory.Seed(db => TestDb.AddProduct(db, "https://www.noon.com/egypt-en/a/N1/p/", "Existing", source: ProductSource.UserAdded));

        var response = await Submit(factory.CreateClient(), "https://www.noon.com/egypt-en/b/N2/p/");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Seeded_products_do_not_count_toward_the_user_limits()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxUserProducts"] = "1" });
        factory.Seed(db =>
        {
            for (var i = 1; i <= 5; i++)
            {
                TestDb.AddProduct(db, $"https://www.noon.com/egypt-en/s{i}/S{i}/p/", $"Seed {i}", source: ProductSource.Seed);
            }
        });

        await Created(await Submit(factory.CreateClient(), Url));
    }

    [Fact]
    public async Task The_product_endpoint_reports_the_crawl_while_it_runs_and_after_it_fails()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var product = await Created(await Submit(client, Url));

        var pending = await client.GetFromJsonAsync<ProductDetailDto>($"/api/products/{product.Id}", Json.Options);
        Assert.Equal("Pending", pending!.Crawl!.Status);

        factory.Seed(db =>
        {
            db.ProductCrawlRequests.Where(r => r.ProductId == product.Id).ExecuteUpdate(s => s
                .SetProperty(r => r.Status, JobStatus.Running)
                .SetProperty(r => r.StartedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                .SetProperty(r => r.GitHubRunId, 987654321L));
        });
        var running = await client.GetFromJsonAsync<ProductDetailDto>($"/api/products/{product.Id}", Json.Options);
        Assert.Equal("Running", running!.Crawl!.Status);
        Assert.EndsWith("/actions/runs/987654321", running.Crawl.RunUrl);

        factory.Seed(db =>
        {
            db.ProductCrawlRequests.Where(r => r.ProductId == product.Id).ExecuteUpdate(s => s
                .SetProperty(r => r.Status, JobStatus.Failed)
                .SetProperty(r => r.FailureStage, "scrape")
                .SetProperty(r => r.ErrorMessage, "noon.com returned HTTP 404"));
        });
        var failed = await client.GetFromJsonAsync<ProductDetailDto>($"/api/products/{product.Id}", Json.Options);
        Assert.Equal(("Failed", "scrape", "noon.com returned HTTP 404"),
            (failed!.Crawl!.Status, failed.Crawl.FailureStage, failed.Crawl.ErrorMessage));
    }

    // A worker that vanished (runner killed, dispatch lost) must not leave the UI
    // "crawling..." forever: past the deadline the endpoint reads it as failed.
    [Fact]
    public async Task A_crawl_that_never_reports_back_reads_as_timed_out()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var product = await Created(await Submit(client, Url));

        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        var later = await client.GetFromJsonAsync<ProductDetailDto>($"/api/products/{product.Id}", Json.Options);
        Assert.Equal(("Failed", "timeout"), (later!.Crawl!.Status, later.Crawl.FailureStage));
    }

    [Fact]
    public async Task A_product_with_no_crawl_request_has_no_crawl_status()
    {
        using var factory = new ApiFactory();
        int id = 0;
        factory.Seed(db => id = TestDb.AddProduct(db).Id);

        var product = await factory.CreateClient().GetFromJsonAsync<ProductDetailDto>($"/api/products/{id}", Json.Options);

        Assert.Null(product!.Crawl);
    }
}

public class CheckNowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<int> ProductAsync(ApiFactory factory)
    {
        var id = 0;
        factory.Seed(db => id = TestDb.AddProduct(db).Id);
        return await Task.FromResult(id);
    }

    [Fact]
    public async Task Check_now_for_an_unknown_product_is_a_404()
    {
        using var factory = new ApiFactory();

        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().PostAsync("/api/products/999/check-now", null)).StatusCode);
    }

    [Fact]
    public async Task Check_now_records_a_pending_request_and_dispatches_it()
    {
        using var factory = new ApiFactory();
        var productId = await ProductAsync(factory);

        var response = await factory.CreateClient().PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options))!;
        Assert.Equal("Pending", accepted.Status);
        Assert.Equal([accepted.RequestId], factory.Dispatch.CheckedRequestIds);
        Assert.Contains($"/check-now/{accepted.RequestId}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Asking_again_while_a_check_is_active_returns_that_check()
    {
        using var factory = new ApiFactory(new() { ["RateLimiting:DispatchPerMinute"] = "100" });
        var productId = await ProductAsync(factory);
        var client = factory.CreateClient();

        var first = await (await client.PostAsync($"/api/products/{productId}/check-now", null)).Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options);
        var second = await (await client.PostAsync($"/api/products/{productId}/check-now", null)).Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options);

        Assert.Equal(first!.RequestId, second!.RequestId);
        Assert.Single(factory.Dispatch.Dispatched);
    }

    [Fact]
    public async Task Simultaneous_requests_for_one_product_start_one_check()
    {
        using var factory = new ApiFactory(new() { ["RateLimiting:DispatchPerMinute"] = "1000", ["RateLimiting:DispatchTotalPerMinute"] = "1000" });
        var productId = await ProductAsync(factory);
        var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => client.PostAsync($"/api/products/{productId}/check-now", null))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options))!.RequestId));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, factory.Query(db => db.CheckNowRequests.Count()));
        Assert.Single(factory.Dispatch.Dispatched);
    }

    [Fact]
    public async Task A_new_check_is_allowed_once_the_previous_one_finished()
    {
        using var factory = new ApiFactory(new() { ["RateLimiting:DispatchPerMinute"] = "100" });
        var productId = await ProductAsync(factory);
        var client = factory.CreateClient();
        var first = await (await client.PostAsync($"/api/products/{productId}/check-now", null)).Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options);
        factory.Seed(db => db.CheckNowRequests.Where(r => r.Id == first!.RequestId)
            .ExecuteUpdate(s => s.SetProperty(r => r.Status, JobStatus.Completed)));

        var second = await (await client.PostAsync($"/api/products/{productId}/check-now", null)).Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options);

        Assert.NotEqual(first!.RequestId, second!.RequestId);
        Assert.Equal(2, factory.Dispatch.Dispatched.Count);
    }

    // The previous check's worker died. Its row still says Pending, and the unique
    // index would refuse a new request forever - unless a stale request is expired
    // first, which is what happens here.
    [Fact]
    public async Task A_dead_check_does_not_block_the_product_forever()
    {
        using var factory = new ApiFactory(new() { ["RateLimiting:DispatchPerMinute"] = "100" });
        var productId = await ProductAsync(factory);
        var client = factory.CreateClient();
        var first = await (await client.PostAsync($"/api/products/{productId}/check-now", null)).Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options);

        factory.Clock.Advance(TimeSpan.FromMinutes(20));
        var second = await (await client.PostAsync($"/api/products/{productId}/check-now", null)).Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options);

        Assert.NotEqual(first!.RequestId, second!.RequestId);
        var expired = factory.Query(db => db.CheckNowRequests.Single(r => r.Id == first.RequestId));
        Assert.Equal((JobStatus.Failed, "timeout"), (expired.Status, expired.FailureStage));
    }

    [Fact]
    public async Task A_failed_dispatch_is_a_502_and_the_request_is_closed_as_failed()
    {
        using var factory = new ApiFactory();
        var productId = await ProductAsync(factory);
        factory.Dispatch.ShouldFail = true;

        var response = await factory.CreateClient().PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var request = factory.Query(db => db.CheckNowRequests.Single());
        Assert.Equal((JobStatus.Failed, "dispatch"), (request.Status, request.FailureStage));
        Assert.NotNull(request.CompletedAt);
    }

    [Fact]
    public async Task After_a_failed_dispatch_the_user_can_simply_try_again()
    {
        using var factory = new ApiFactory(new() { ["RateLimiting:DispatchPerMinute"] = "100" });
        var productId = await ProductAsync(factory);
        var client = factory.CreateClient();
        factory.Dispatch.ShouldFail = true;
        await client.PostAsync($"/api/products/{productId}/check-now", null);

        factory.Dispatch.ShouldFail = false;
        var retry = await client.PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    [Fact]
    public async Task Over_the_dispatch_budget_a_check_is_refused_with_a_429()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxDispatchesPerHour"] = "0" });
        var productId = await ProductAsync(factory);

        var response = await factory.CreateClient().PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(factory.Dispatch.Dispatched);
        Assert.Equal(0, factory.Query(db => db.CheckNowRequests.Count()));
    }

    [Fact]
    public async Task The_budget_counts_crawls_and_checks_together()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxDispatchesPerHour"] = "2", ["RateLimiting:DispatchPerMinute"] = "100" });
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/products", new CreateProductRequestDto { Url = "https://www.noon.com/egypt-en/a/N1/p/" });
        await client.PostAsJsonAsync("/api/products", new CreateProductRequestDto { Url = "https://www.noon.com/egypt-en/b/N2/p/" });
        var productId = factory.Query(db => db.Products.First().Id);

        var check = await client.PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, check.StatusCode);
    }

    [Fact]
    public async Task Budget_deferred_requests_do_not_deepen_the_budget()
    {
        using var factory = new ApiFactory(new() { ["Jobs:MaxDispatchesPerHour"] = "1", ["RateLimiting:DispatchPerMinute"] = "100" });
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/products", new CreateProductRequestDto { Url = "https://www.noon.com/egypt-en/a/N1/p/" });
        // Over budget: recorded as deferred, never dispatched.
        await client.PostAsJsonAsync("/api/products", new CreateProductRequestDto { Url = "https://www.noon.com/egypt-en/b/N2/p/" });

        // The hour passes; one real dispatch was made, so the budget of 1 is free again.
        factory.Clock.Advance(TimeSpan.FromHours(2));
        var productId = factory.Query(db => db.Products.First().Id);
        var check = await client.PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.Accepted, check.StatusCode);
    }

    [Fact]
    public async Task A_pending_check_reads_as_pending_with_no_offers()
    {
        using var factory = new ApiFactory();
        int productId = 0, requestId = 0;
        factory.Seed(db =>
        {
            productId = TestDb.AddProduct(db).Id;
            var request = new CheckNowRequest { ProductId = productId, RequestedAt = DateTimeOffset.UtcNow };
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
                ProductId = productId, Status = JobStatus.Completed, RequestedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, ResultJson = resultJson, GitHubRunId = 555
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
        Assert.EndsWith("/actions/runs/555", result.RunUrl);
    }

    [Fact]
    public async Task A_failed_check_reports_the_stage_and_message()
    {
        using var factory = new ApiFactory();
        int productId = 0, requestId = 0;
        factory.Seed(db =>
        {
            productId = TestDb.AddProduct(db).Id;
            var request = new CheckNowRequest
            {
                ProductId = productId, Status = JobStatus.Failed, RequestedAt = DateTimeOffset.UtcNow,
                FailureStage = "scrape", ErrorMessage = "No offers were found on the product page."
            };
            db.CheckNowRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
        });

        var result = await factory.CreateClient()
            .GetFromJsonAsync<CheckNowResultDto>($"/api/products/{productId}/check-now/{requestId}", Json.Options);

        Assert.Equal(("Failed", "scrape", "No offers were found on the product page."),
            (result!.Status, result.FailureStage, result.ErrorMessage));
    }

    [Fact]
    public async Task A_check_nobody_picked_up_reads_as_failed_after_the_deadline()
    {
        using var factory = new ApiFactory();
        var productId = await ProductAsync(factory);
        var client = factory.CreateClient();
        var accepted = await (await client.PostAsync($"/api/products/{productId}/check-now", null)).Content.ReadFromJsonAsync<CheckNowAcceptedDto>(Json.Options);

        factory.Clock.Advance(TimeSpan.FromMinutes(20));

        var result = await client.GetFromJsonAsync<CheckNowResultDto>($"/api/products/{productId}/check-now/{accepted!.RequestId}", Json.Options);
        Assert.Equal(("Failed", "timeout"), (result!.Status, result.FailureStage));
    }

    [Fact]
    public async Task A_check_request_is_only_visible_under_its_own_product()
    {
        using var factory = new ApiFactory();
        int otherProductId = 0, requestId = 0;
        factory.Seed(db =>
        {
            var product = TestDb.AddProduct(db, url: "https://www.noon.com/egypt-en/a/N1/p/");
            otherProductId = TestDb.AddProduct(db, url: "https://www.noon.com/egypt-en/b/N2/p/").Id;
            var request = new CheckNowRequest { ProductId = product.Id, RequestedAt = T0 };
            db.CheckNowRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
        });

        var response = await factory.CreateClient().GetAsync($"/api/products/{otherProductId}/check-now/{requestId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_corrupt_stored_result_reads_as_a_failed_check_not_a_crash()
    {
        using var factory = new ApiFactory();
        int productId = 0, requestId = 0;
        factory.Seed(db =>
        {
            productId = TestDb.AddProduct(db).Id;
            var request = new CheckNowRequest
            {
                ProductId = productId, Status = JobStatus.Completed, RequestedAt = DateTimeOffset.UtcNow, ResultJson = "{ not json"
            };
            db.CheckNowRequests.Add(request);
            db.SaveChanges();
            requestId = request.Id;
        });

        var result = await factory.CreateClient()
            .GetFromJsonAsync<CheckNowResultDto>($"/api/products/{productId}/check-now/{requestId}", Json.Options);

        Assert.Equal(("Failed", "result", "The stored result could not be read."),
            (result!.Status, result.FailureStage, result.ErrorMessage));
        Assert.Null(result.Offers);
    }
}
