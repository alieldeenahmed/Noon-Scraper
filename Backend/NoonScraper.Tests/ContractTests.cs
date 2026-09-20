using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

// The JSON the API actually produces, pinned in files under Contracts/.
//
// The frontend has no code generator; it declares the response shapes itself
// (Frontend/src/api/schemas.ts) and validates every response against them. What
// keeps the two sides honest is that the frontend's contract.test.ts parses these
// very files with those schemas. So:
//   - change a DTO here and this test fails until the fixtures are regenerated
//     (which is a visible diff in review), and
//   - regenerated fixtures the frontend's schemas don't accept fail the frontend's
//     tests - the mismatch is caught in CI, not by a user staring at a blank page.
//
// Regenerate with:  NOON_UPDATE_CONTRACTS=1 dotnet test --filter ContractTests
public class ContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string ContractsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "NoonScraper.Tests", "Contracts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not find NoonScraper.Tests/Contracts above " + AppContext.BaseDirectory);
    }

    private static async Task AssertContractAsync(string name, HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var actual = Normalize(body);

        var path = Path.Combine(ContractsDirectory(), name + ".json");
        if (Environment.GetEnvironmentVariable("NOON_UPDATE_CONTRACTS") == "1")
        {
            await File.WriteAllTextAsync(path, actual + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"Missing contract fixture {name}.json - regenerate with NOON_UPDATE_CONTRACTS=1.");
        var expected = Normalize(await File.ReadAllTextAsync(path));
        Assert.True(
            expected == actual,
            $"The API's response for '{name}' no longer matches Contracts/{name}.json.\n" +
            "If the change is intended, regenerate the fixtures (NOON_UPDATE_CONTRACTS=1) and make sure the " +
            "frontend's schemas (Frontend/src/api/schemas.ts) still accept them.\n\nExpected:\n" + expected + "\n\nActual:\n" + actual);
    }

    // Drop the per-request trace id (it's a fresh value each time) and format
    // consistently, so the comparison is about shape and values.
    private static string Normalize(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            obj.Remove("traceId");
        }

        return node.ToJsonString(Pretty).ReplaceLineEndings("\n");
    }

    private static ApiFactory Factory()
    {
        // A fixed clock, so the stale-request view and every timestamp are the same on
        // every run and every machine.
        return new ApiFactory(new() { ["RateLimiting:DispatchPerMinute"] = "100" }, startTime: T0.AddMinutes(1));
    }

    // A small, fully specified data set: one crawled product with a flag and a
    // restock, one uncrawled product with a pending crawl, one whose crawl failed.
    private static (int Crawled, int Pending, int Failed) Seed(ApiFactory factory)
    {
        var ids = (0, 0, 0);
        factory.Seed(db =>
        {
            var crawled = new Product
            {
                Url = "https://www.noon.com/egypt-en/galaxy-phone/N100/p/",
                NoonProductId = "N100",
                Name = "Galaxy Phone 128GB",
                Category = Category.Mobiles,
                MerchantName = "noon",
                Rating = 4.5m,
                Source = ProductSource.Seed,
                AddedAt = T0.AddDays(-30)
            };
            var pending = new Product
            {
                Url = "https://www.noon.com/egypt-en/new-thing/N200/p/",
                NoonProductId = "N200",
                Source = ProductSource.UserAdded,
                AddedAt = T0
            };
            var failed = new Product
            {
                Url = "https://www.noon.com/egypt-en/broken-thing/N300/p/",
                NoonProductId = "N300",
                Source = ProductSource.UserAdded,
                AddedAt = T0
            };
            db.Products.AddRange(crawled, pending, failed);
            db.SaveChanges();

            var first = TestDb.AddSnapshot(db, crawled, 2500, crawledAt: T0.AddDays(-10));
            var second = TestDb.AddSnapshot(db, crawled, 1999, crawledAt: T0.AddDays(-1), discountPercent: 20);
            db.DiscountFlags.Add(new DiscountFlag
            {
                ProductId = crawled.Id,
                TriggeringSnapshotId = second.Id,
                PriorHighPrice = 2500,
                PriorHighDetectedAt = first.CrawledAt,
                HistoricalLowPrice = 1800,
                DiscountedPrice = 1999,
                DiscountPercent = 20,
                DetectedAt = second.CrawledAt
            });
            db.RestockEvents.Add(new RestockEvent { ProductId = crawled.Id, TriggeringSnapshotId = second.Id, DetectedAt = second.CrawledAt });

            db.ProductCrawlRequests.Add(new ProductCrawlRequest
            {
                ProductId = pending.Id,
                Status = JobStatus.Pending,
                RequestedAt = T0,
                CorrelationId = Guid.Parse("00000000-0000-0000-0000-000000000001")
            });
            db.ProductCrawlRequests.Add(new ProductCrawlRequest
            {
                ProductId = failed.Id,
                Status = JobStatus.Failed,
                RequestedAt = T0,
                StartedAt = T0.AddSeconds(20),
                CompletedAt = T0.AddSeconds(45),
                FailureStage = "scrape",
                ErrorMessage = "The page had no product data (it may have been blocked).",
                GitHubRunId = 123456789,
                CorrelationId = Guid.Parse("00000000-0000-0000-0000-000000000002")
            });
            db.SaveChanges();

            ids = (crawled.Id, pending.Id, failed.Id);
        });
        return ids;
    }

    [Fact]
    public async Task Product_list_and_stats()
    {
        using var factory = Factory();
        Seed(factory);
        var client = factory.CreateClient();

        await AssertContractAsync("product-list", await client.GetAsync("/api/products?sortBy=price&sortDir=desc&page=1&pageSize=25"), HttpStatusCode.OK);
        await AssertContractAsync("product-stats", await client.GetAsync("/api/products/stats"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Product_detail_for_each_crawl_state()
    {
        using var factory = Factory();
        var (crawled, pending, failed) = Seed(factory);
        var client = factory.CreateClient();

        await AssertContractAsync("product-detail-crawled", await client.GetAsync($"/api/products/{crawled}"), HttpStatusCode.OK);
        await AssertContractAsync("product-detail-crawl-pending", await client.GetAsync($"/api/products/{pending}"), HttpStatusCode.OK);
        await AssertContractAsync("product-detail-crawl-failed", await client.GetAsync($"/api/products/{failed}"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task History_flags_and_restocks()
    {
        using var factory = Factory();
        var (crawled, _, _) = Seed(factory);
        var client = factory.CreateClient();

        await AssertContractAsync("price-history", await client.GetAsync($"/api/products/{crawled}/history"), HttpStatusCode.OK);
        await AssertContractAsync("discount-flags", await client.GetAsync($"/api/products/{crawled}/discount-flags"), HttpStatusCode.OK);
        await AssertContractAsync("restocks", await client.GetAsync($"/api/products/{crawled}/restocks"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Check_now_accepted_and_each_result_state()
    {
        using var factory = Factory();
        var (crawled, _, _) = Seed(factory);
        var client = factory.CreateClient();
        int completed = 0, failed = 0, running = 0;
        factory.Seed(db =>
        {
            CheckNowRequest Make(JobStatus status) => new()
            {
                ProductId = crawled,
                Status = status,
                RequestedAt = T0,
                CorrelationId = Guid.Parse("00000000-0000-0000-0000-0000000000ff")
            };

            var done = Make(JobStatus.Completed);
            done.StartedAt = T0.AddSeconds(15);
            done.CompletedAt = T0.AddSeconds(50);
            done.GitHubRunId = 555;
            // Written exactly the way the crawler writes it (CheckNowJob), so this also
            // pins that what the crawler stores is what the API can read back.
            done.ResultJson = JsonSerializer.Serialize(new[]
            {
                new NoonScraper.Crawler.OfferResult { MerchantName = "Seller A", Price = 1850m, Rating = 4.7m },
                new NoonScraper.Crawler.OfferResult { MerchantName = "Seller B", Price = 1990m }
            });

            var bad = Make(JobStatus.Failed);
            bad.StartedAt = T0.AddSeconds(15);
            bad.CompletedAt = T0.AddSeconds(40);
            bad.FailureStage = "scrape";
            bad.ErrorMessage = "The page had no product data (it may have been blocked).";
            bad.GitHubRunId = 556;

            var busy = Make(JobStatus.Running);
            busy.StartedAt = T0.AddSeconds(15);
            busy.GitHubRunId = 557;

            // One active check per product, so the finished ones go first.
            db.CheckNowRequests.AddRange(done, bad);
            db.SaveChanges();
            db.CheckNowRequests.Add(busy);
            db.SaveChanges();
            (completed, failed, running) = (done.Id, bad.Id, busy.Id);
        });

        await AssertContractAsync("check-now-result-completed", await client.GetAsync($"/api/products/{crawled}/check-now/{completed}"), HttpStatusCode.OK);
        await AssertContractAsync("check-now-result-failed", await client.GetAsync($"/api/products/{crawled}/check-now/{failed}"), HttpStatusCode.OK);
        await AssertContractAsync("check-now-result-running", await client.GetAsync($"/api/products/{crawled}/check-now/{running}"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Check_now_accepted()
    {
        using var factory = Factory();
        var (crawled, _, _) = Seed(factory);

        var response = await factory.CreateClient().PostAsync($"/api/products/{crawled}/check-now", null);

        await AssertContractAsync("check-now-accepted", response, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Errors_are_problem_details_with_the_extras_the_frontend_reads()
    {
        using var factory = Factory();
        Seed(factory);
        var client = factory.CreateClient();

        var invalid = await client.PostAsJsonAsync("/api/products", new { url = "http://example.com/x" });
        await AssertContractAsync("problem-invalid-url", invalid, HttpStatusCode.BadRequest);

        var duplicate = await client.PostAsJsonAsync("/api/products", new { url = "https://www.noon.com/egypt-en/galaxy-phone/N100/p/" });
        await AssertContractAsync("problem-already-tracked", duplicate, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Creating_a_product_returns_the_detail_shape()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/products", new { url = "https://www.noon.com/egypt-en/fresh-thing/N400/p/" });

        // The new row's id, added-at time and correlation id vary run to run, so
        // this pins the shape (and the crawl block being present), not the values.
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            ["id", "url", "noonProductId", "name", "category", "merchantName", "rating", "source", "isActive", "addedAt",
             "latestPrice", "latestDiscountPercent", "latestStock", "lastCrawledAt", "crawl"],
            json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            ["requestId", "status", "failureStage", "errorMessage", "requestedAt", "startedAt", "completedAt", "runUrl"],
            json.GetProperty("crawl").EnumerateObject().Select(p => p.Name).ToArray());
    }
}
