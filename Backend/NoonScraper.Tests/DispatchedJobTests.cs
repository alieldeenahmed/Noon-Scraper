using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoonScraper.Crawler;
using NoonScraper.Crawler.Jobs;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

// The two on-demand jobs share a lifecycle (DispatchedJob): claim, work, record the
// outcome. These tests drive it with a scripted browser, on a real database.
public class CheckNowJobTests
{
    private static IReadOnlyList<OfferResult> Offers(params (string Merchant, decimal Price)[] offers) =>
        offers.Select(o => new OfferResult { MerchantName = o.Merchant, Price = o.Price }).ToList();

    private static CheckNowRequest Saved(CrawlHarness h, int id) =>
        h.Query(db => db.CheckNowRequests.AsNoTracking().Single(r => r.Id == id));

    public class Success
    {
        [Fact]
        public async Task Stores_the_offers_cheapest_first_and_completes_the_request()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) => Task.FromResult(Offers(("noon", 300), ("Wi-Tech", 250), ("iQ", 275)));

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, gitHubRunId: 777, CancellationToken.None);

            Assert.Equal(ExitCodes.Success, exit);
            var saved = Saved(h, request.Id);
            Assert.Equal(JobStatus.Completed, saved.Status);
            Assert.Equal(777, saved.GitHubRunId);
            Assert.NotNull(saved.StartedAt);
            Assert.NotNull(saved.CompletedAt);
            var stored = JsonSerializer.Deserialize<List<OfferResult>>(saved.ResultJson!)!;
            Assert.Equal(["Wi-Tech", "iQ", "noon"], stored.Select(o => o.MerchantName));
        }

        [Fact]
        public async Task Scrapes_the_products_own_url()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct("N77");
            var request = h.AddCheckNow(product);
            h.Session.Offers = (_, _) => Task.FromResult(Offers(("noon", 1)));

            await h.NewCheckNowJob().RunAsync(request.Id, null, CancellationToken.None);

            Assert.Equal([$"offers:{product.Url}"], h.Session.Calls);
        }
    }

    public class Failure
    {
        [Fact]
        public async Task No_offers_fails_the_request_at_the_scrape_stage()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) => Task.FromResult(Offers());

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, null, CancellationToken.None);

            Assert.Equal(ExitCodes.Failure, exit);
            var saved = Saved(h, request.Id);
            Assert.Equal((JobStatus.Failed, "scrape"), (saved.Status, saved.FailureStage));
            Assert.Equal("No offers were found on the product page.", saved.ErrorMessage);
            Assert.Null(saved.ResultJson);
        }

        [Fact]
        public async Task A_browser_that_will_not_start_fails_at_the_launch_stage()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Sessions.OpenFailure = new InvalidOperationException("Chrome not found at /opt/secret/path");

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, null, CancellationToken.None);

            Assert.Equal(ExitCodes.Failure, exit);
            var saved = Saved(h, request.Id);
            Assert.Equal((JobStatus.Failed, "launch-browser"), (saved.Status, saved.FailureStage));
        }

        // Exception text from libraries can contain paths, hosts, or credentials, and
        // whatever lands on the row is served by the public API.
        [Fact]
        public async Task An_unexpected_exceptions_message_never_reaches_the_row()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) => throw new InvalidOperationException("Host=db.internal;Password=hunter2");

            await h.NewCheckNowJob().RunAsync(request.Id, null, CancellationToken.None);

            var saved = Saved(h, request.Id);
            Assert.DoesNotContain("hunter2", saved.ErrorMessage);
            Assert.DoesNotContain("db.internal", saved.ErrorMessage);
            Assert.Equal("Unexpected InvalidOperationException; see the workflow run logs.", saved.ErrorMessage);
        }

        [Fact]
        public async Task A_transient_scrape_failure_is_retried_on_a_fresh_page()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            var attempts = 0;
            h.Session.Offers = (_, _) => ++attempts == 1
                ? throw new TimeoutException("page never loaded")
                : Task.FromResult(Offers(("noon", 100)));

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, null, CancellationToken.None);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(2, attempts);
            Assert.Equal(1, h.Session.ResetCount);
            Assert.Equal(JobStatus.Completed, Saved(h, request.Id).Status);
        }

        [Fact]
        public async Task A_permanent_failure_is_not_retried()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) => throw new ScrapeParseException("Product data has no valid (positive) price.");

            await h.NewCheckNowJob().RunAsync(request.Id, null, CancellationToken.None);

            Assert.Equal(1, h.Session.CallsTo("offers"));
            Assert.Equal(0, h.Session.ResetCount);
            Assert.Equal("Product data has no valid (positive) price.", Saved(h, request.Id).ErrorMessage);
        }

        [Fact]
        public async Task A_404_is_not_retried_and_a_503_is()
        {
            using var h404 = new CrawlHarness();
            var gone = h404.AddCheckNow(h404.AddProduct());
            h404.Session.Offers = (_, _) => throw new ScrapeNavigationException(404, "u");
            await h404.NewCheckNowJob().RunAsync(gone.Id, null, CancellationToken.None);
            Assert.Equal(1, h404.Session.CallsTo("offers"));

            using var h503 = new CrawlHarness();
            var busy = h503.AddCheckNow(h503.AddProduct());
            h503.Session.Offers = (_, _) => throw new ScrapeNavigationException(503, "u");
            await h503.NewCheckNowJob().RunAsync(busy.Id, null, CancellationToken.None);
            Assert.Equal(2, h503.Session.CallsTo("offers"));
            Assert.Equal("noon.com returned HTTP 503", Saved(h503, busy.Id).ErrorMessage);
        }

        [Fact]
        public async Task Retrying_gives_up_after_the_configured_attempts_and_reports_the_last_failure()
        {
            using var h = new CrawlHarness();
            h.Options.MaxAttempts = 3;
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) => throw new TimeoutException("slow");

            await h.NewCheckNowJob().RunAsync(request.Id, null, CancellationToken.None);

            Assert.Equal(3, h.Session.CallsTo("offers"));
            Assert.Equal(2, h.Session.ResetCount);
            Assert.Equal(JobStatus.Failed, Saved(h, request.Id).Status);
        }

        [Fact]
        public async Task A_request_that_does_not_exist_is_a_failure_and_touches_nothing()
        {
            using var h = new CrawlHarness();

            var exit = await h.NewCheckNowJob().RunAsync(12345, null, CancellationToken.None);

            Assert.Equal(ExitCodes.Failure, exit);
            Assert.Equal(0, h.Sessions.OpenCount);
        }
    }

    // GitHub can deliver a dispatch twice (a re-run, a retried API call), and two
    // runners can start at once. The atomic claim means the second does nothing.
    public class DuplicateDelivery
    {
        [Fact]
        public async Task A_request_already_handled_is_not_scraped_again()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) => Task.FromResult(Offers(("noon", 100)));

            await h.NewCheckNowJob().RunAsync(request.Id, 1, CancellationToken.None);
            var second = await h.NewCheckNowJob().RunAsync(request.Id, 2, CancellationToken.None);

            Assert.Equal(ExitCodes.Success, second);
            Assert.Equal(1, h.Session.CallsTo("offers"));
            Assert.Equal(1, Saved(h, request.Id).GitHubRunId);
        }

        [Fact]
        public async Task Two_workers_started_at_once_scrape_once()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = async (_, _) =>
            {
                await Task.Delay(300);
                return Offers(("noon", 100));
            };

            var exits = await Task.WhenAll(Enumerable.Range(1, 5)
                .Select(i => Task.Run(() => h.NewCheckNowJob().RunAsync(request.Id, i, CancellationToken.None))));

            Assert.All(exits, e => Assert.Equal(ExitCodes.Success, e));
            Assert.Equal(1, h.Session.CallsTo("offers"));
            Assert.Equal(JobStatus.Completed, Saved(h, request.Id).Status);
        }

        [Fact]
        public async Task A_request_that_was_already_expired_is_left_alone()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct(), JobStatus.Failed);

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, 1, CancellationToken.None);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(0, h.Sessions.OpenCount);
            Assert.Equal(JobStatus.Failed, Saved(h, request.Id).Status);
        }
    }

    public class Timeouts
    {
        // The worker is slow and the API expires the request in the meantime. The
        // worker's late result must not resurrect a request everyone was told failed.
        [Fact]
        public async Task A_result_arriving_after_the_request_was_expired_is_discarded()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) =>
            {
                // Meanwhile, the API (a different process) times the request out.
                using var api = h.NewContext();
                JobLifecycle_TryFail(api, request.Id);
                return Task.FromResult(Offers(("noon", 100)));
            };

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, 1, CancellationToken.None);

            Assert.Equal(ExitCodes.Success, exit);
            var saved = Saved(h, request.Id);
            Assert.Equal((JobStatus.Failed, "timeout"), (saved.Status, saved.FailureStage));
            Assert.Null(saved.ResultJson);
        }

        private static void JobLifecycle_TryFail(NoonScraper.Data.AppDbContext db, int id) =>
            NoonScraper.Data.JobLifecycle.TryFailAsync(db.CheckNowRequests, id, "timeout", "Timed out", DateTimeOffset.UtcNow)
                .GetAwaiter().GetResult();
    }

    public class Cancellation
    {
        // GitHub cancels a run (or its timeout hits) with SIGINT/SIGTERM, which the
        // crawler turns into a token. The request must not be left Running.
        [Fact]
        public async Task A_cancelled_job_records_that_it_was_cancelled()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            using var cts = new CancellationTokenSource();
            h.Session.Offers = async (_, ct) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return Offers();
            };

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, 1, cts.Token);

            Assert.Equal(ExitCodes.Cancelled, exit);
            var saved = Saved(h, request.Id);
            Assert.Equal((JobStatus.Failed, "cancelled"), (saved.Status, saved.FailureStage));
            Assert.Contains("Cancelled", saved.ErrorMessage);
        }

        [Fact]
        public async Task A_job_cancelled_before_it_starts_leaves_the_request_pending_for_expiry()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                h.NewCheckNowJob().RunAsync(request.Id, 1, cts.Token));

            // Never claimed: still Pending, so the stale sweep (or a re-run) handles it.
            Assert.Equal(JobStatus.Pending, Saved(h, request.Id).Status);
        }
    }

    public class Observability
    {
        // "User submitted X -> job Y -> failed at stage Z": every line the job logs
        // carries the ids that connect it to the request, the product, and the
        // GitHub run, so a log search on any one of them finds the whole story.
        [Fact]
        public async Task Every_log_line_carries_the_request_product_correlation_and_run_ids()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            var request = h.AddCheckNow(product);
            h.Session.Offers = (_, _) => Task.FromResult(Offers(("noon", 100)));

            await h.NewCheckNowJob().RunAsync(request.Id, gitHubRunId: 4242, CancellationToken.None);

            Assert.NotEmpty(h.CheckNowLog.Lines);
            Assert.All(h.CheckNowLog.Lines, line =>
            {
                Assert.Equal("check-now", line.Scope["Job"]);
                Assert.Equal(request.Id, line.Scope["RequestId"]);
                Assert.Equal(product.Id, line.Scope["ProductId"]);
                Assert.Equal(request.CorrelationId, line.Scope["CorrelationId"]);
                Assert.Equal(4242L, line.Scope["GitHubRunId"]);
            });
        }

        [Fact]
        public async Task A_failure_is_logged_with_its_stage_and_the_full_exception()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());
            h.Session.Offers = (_, _) => throw new InvalidOperationException("the real, detailed reason");

            await h.NewCheckNowJob().RunAsync(request.Id, 1, CancellationToken.None);

            var error = Assert.Single(h.CheckNowLog.Lines, l => l.Level == LogLevel.Error);
            Assert.Contains("stage scrape", error.Message);
            Assert.Equal("the real, detailed reason", error.Exception!.Message);
            Assert.Equal(request.CorrelationId, error.Scope["CorrelationId"]);
        }
    }
}

public class CrawlProductJobTests
{
    private static ProductCrawlRequest Saved(CrawlHarness h, int id) =>
        h.Query(db => db.ProductCrawlRequests.AsNoTracking().Single(r => r.Id == id));

    [Fact]
    public async Task Records_the_reading_and_completes_the_request()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N5");
        var request = h.AddCrawl(product);
        h.Session.Product = (_, _) => Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped("N5", 250));

        var exit = await h.NewCrawlProductJob().RunAsync(request.Id, 99, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(JobStatus.Completed, Saved(h, request.Id).Status);
        var stored = h.Query(db => db.Products.Single());
        Assert.Equal("Item N5", stored.Name);
        Assert.Equal(250, h.Query(db => db.PriceSnapshots.Single()).Price);
    }

    [Fact]
    public async Task Fills_in_the_product_that_was_submitted_even_if_the_page_reports_another_spelling_of_its_url()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N5");
        var request = h.AddCrawl(product);
        var scraped = CrawlHarness.Scraped("N5", 250);
        scraped.Url = "https://www.noon.com/egypt-en/a-different-slug/N5/p/";
        h.Session.Product = (_, _) => Task.FromResult<ScrapedProduct?>(scraped);

        await h.NewCrawlProductJob().RunAsync(request.Id, 1, CancellationToken.None);

        // No second product row: the reading went onto the submitted one.
        Assert.Equal(1, h.Query(db => db.Products.Count()));
        Assert.Equal(product.Id, h.Query(db => db.PriceSnapshots.Single()).ProductId);
    }

    [Fact]
    public async Task A_blocked_page_with_no_data_is_retried_then_fails_with_a_clear_message()
    {
        using var h = new CrawlHarness();
        var request = h.AddCrawl(h.AddProduct());
        h.Session.Product = (_, _) => Task.FromResult<ScrapedProduct?>(null);

        await h.NewCrawlProductJob().RunAsync(request.Id, 1, CancellationToken.None);

        Assert.Equal(2, h.Session.CallsTo("product"));
        var saved = Saved(h, request.Id);
        Assert.Equal((JobStatus.Failed, "scrape"), (saved.Status, saved.FailureStage));
        Assert.Contains("may have been blocked", saved.ErrorMessage);
    }

    [Fact]
    public async Task A_reading_that_cannot_be_stored_fails_at_the_persist_stage()
    {
        using var h = new CrawlHarness();
        var request = h.AddCrawl(h.AddProduct("N5"));
        h.Session.Product = (_, _) => Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped("N5", price: 0));

        await h.NewCrawlProductJob().RunAsync(request.Id, 1, CancellationToken.None);

        var saved = Saved(h, request.Id);
        Assert.Equal((JobStatus.Failed, "persist"), (saved.Status, saved.FailureStage));
        Assert.Empty(h.Query(db => db.PriceSnapshots.ToList()));
    }

    [Fact]
    public async Task A_page_that_does_not_exist_stops_a_never_crawled_product_being_tracked()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N5");
        var request = h.AddCrawl(product);
        h.Session.Product = (_, _) => throw new ScrapeNavigationException(404, "u");

        await h.NewCrawlProductJob().RunAsync(request.Id, 1, CancellationToken.None);

        Assert.False(h.Query(db => db.Products.Single()).IsActive);
        Assert.Equal(1, h.Session.CallsTo("product"));
    }

    [Fact]
    public async Task A_404_never_ends_tracking_of_a_product_that_has_history()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N5");
        TestDb.AddSnapshot(h.Db, product, 100);
        var request = h.AddCrawl(product);
        h.Session.Product = (_, _) => throw new ScrapeNavigationException(404, "u");

        await h.NewCrawlProductJob().RunAsync(request.Id, 1, CancellationToken.None);

        Assert.True(h.Query(db => db.Products.Single()).IsActive);
        Assert.Equal(JobStatus.Failed, Saved(h, request.Id).Status);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task A_temporary_block_never_stops_tracking_a_product(int status)
    {
        using var h = new CrawlHarness();
        var request = h.AddCrawl(h.AddProduct("N5"));
        h.Session.Product = (_, _) => throw new ScrapeNavigationException(status, "u");

        await h.NewCrawlProductJob().RunAsync(request.Id, 1, CancellationToken.None);

        Assert.True(h.Query(db => db.Products.Single()).IsActive);
    }

    [Fact]
    public async Task Two_workers_for_the_same_request_record_one_reading()
    {
        using var h = new CrawlHarness();
        var request = h.AddCrawl(h.AddProduct("N5"));
        h.Session.Product = async (_, _) =>
        {
            await Task.Delay(300);
            return CrawlHarness.Scraped("N5", 100);
        };

        await Task.WhenAll(Enumerable.Range(1, 4)
            .Select(i => Task.Run(() => h.NewCrawlProductJob().RunAsync(request.Id, i, CancellationToken.None))));

        Assert.Equal(1, h.Query(db => db.PriceSnapshots.Count()));
    }

    [Fact]
    public async Task Log_lines_carry_the_ids_that_tie_the_job_to_its_request()
    {
        using var h = new CrawlHarness();
        var product = h.AddProduct("N5");
        var request = h.AddCrawl(product);
        h.Session.Product = (_, _) => Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped("N5", 100));

        await h.NewCrawlProductJob().RunAsync(request.Id, 5, CancellationToken.None);

        Assert.All(h.CrawlProductLog.Lines, line =>
        {
            Assert.Equal("crawl-product", line.Scope["Job"]);
            Assert.Equal(request.CorrelationId, line.Scope["CorrelationId"]);
            Assert.Equal(product.Id, line.Scope["ProductId"]);
        });
    }
}
