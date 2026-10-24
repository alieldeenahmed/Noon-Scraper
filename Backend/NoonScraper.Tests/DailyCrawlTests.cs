using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NoonScraper.Crawler;
using NoonScraper.Crawler.Jobs;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

// The scheduled crawl's failure isolation: what one broken category, one bad
// product, one failed database write, a slow site, an overlapping run, or a
// cancellation does - and doesn't do - to everything else in the run.
public class DailyCrawlTests
{
    private static CategoryScrapeResult Found(params ScrapedProduct[] products) => new(products, []);

    // Serve each category page from a table keyed by category.
    private static void ServeCategories(CrawlHarness h, Func<Category, Task<CategoryScrapeResult>> serve) =>
        h.Session.Category = (url, _) => serve(DailyCrawl.Categories.Single(c => c.Url == url).Category);

    // Serve a product page for every user-added product, from the SKU in its URL.
    private static void ServeProducts(CrawlHarness h, Func<string, decimal>? price = null) =>
        h.Session.Product = (url, _) =>
        {
            var sku = NoonUrl.ExtractSku(url)!;
            return Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped(sku, price?.Invoke(sku) ?? 100));
        };

    private static CrawlRun LastRun(CrawlHarness h) =>
        h.Query(db => db.CrawlRuns.AsNoTracking().OrderByDescending(r => r.Id).First());

    public class HappyPath
    {
        [Fact]
        public async Task Crawls_every_category_and_every_user_added_product()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => Task.FromResult(Found(
                CrawlHarness.Scraped($"{category}A"), CrawlHarness.Scraped($"{category}B"))));
            h.AddProduct("U1");
            h.AddProduct("U2");
            ServeProducts(h);

            var summary = await h.NewDailyCrawl().RunAsync(gitHubRunId: 31337, CancellationToken.None);

            Assert.Equal((5, 0, 12, 12, 0, 0),
                (summary.CategoriesAttempted, summary.CategoriesFailed, summary.ProductsAttempted,
                 summary.ProductsSucceeded, summary.ProductsFailed, summary.ProductsDeferred));
            Assert.Equal(ExitCodes.Success, summary.ExitCode(h.Options));
            Assert.Equal(12, h.Query(db => db.Products.Count()));
            Assert.Equal(12, h.Query(db => db.PriceSnapshots.Count()));

            var run = LastRun(h);
            Assert.Equal(JobStatus.Completed, run.Status);
            Assert.Equal(31337, run.GitHubRunId);
            Assert.Equal(12, run.ProductsSucceeded);
            Assert.NotNull(run.CompletedAt);
            Assert.Contains("12 attempted", run.Summary);
        }

        [Fact]
        public async Task Seeded_products_get_their_category_and_source()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => Task.FromResult(category == Category.Laptops
                ? Found(CrawlHarness.Scraped("L1"))
                : Found()));

            await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            var product = h.Query(db => db.Products.Single());
            Assert.Equal((Category.Laptops, ProductSource.Seed), (product.Category, product.Source));
        }

        [Fact]
        public async Task An_empty_crawl_is_a_success()
        {
            using var h = new CrawlHarness();

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(ExitCodes.Success, summary.ExitCode(h.Options));
            Assert.Equal(5, summary.CategoriesAttempted);
        }

        [Fact]
        public async Task A_product_seen_in_several_places_is_read_once()
        {
            using var h = new CrawlHarness();
            var shared = CrawlHarness.Scraped("SHARED");
            ServeCategories(h, _ => Task.FromResult(Found(shared)));
            var userAdded = h.AddProduct("SHARED");
            ServeProducts(h);

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            // Once via the first category; not again in the other four or as user-added.
            Assert.Equal(1, summary.ProductsAttempted);
            Assert.Equal(0, h.Session.CallsTo("product"));
            Assert.Equal(1, h.Query(db => db.PriceSnapshots.Count(s => s.ProductId == userAdded.Id)));
        }

        [Fact]
        public async Task Inactive_user_products_are_not_visited()
        {
            using var h = new CrawlHarness();
            h.AddProduct("ACTIVE");
            h.AddProduct("ABANDONED", active: false);
            ServeProducts(h);

            await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(1, h.Session.CallsTo("product"));
        }

        [Fact]
        public async Task Skipped_tiles_are_reported_but_are_not_failures()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, _ => Task.FromResult(new CategoryScrapeResult(
                [CrawlHarness.Scraped("OK")], [new SkippedTile("/x", "tile has no price element")])));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(0, summary.ProductsFailed);
            Assert.Equal(ExitCodes.Success, summary.ExitCode(h.Options));
        }

        [Fact]
        public async Task A_user_added_product_is_updated_in_place_whatever_url_spelling_the_page_reports()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct("U1");
            h.Session.Product = (_, _) =>
            {
                var scraped = CrawlHarness.Scraped("U1", 77);
                scraped.Url = "https://www.noon.com/egypt-en/renamed-slug/U1/p/";
                return Task.FromResult<ScrapedProduct?>(scraped);
            };

            await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(1, h.Query(db => db.Products.Count()));
            Assert.Equal(77, h.Query(db => db.PriceSnapshots.Single(s => s.ProductId == product.Id)).Price);
        }
    }

    public class CategoryFailures
    {
        [Fact]
        public async Task A_category_that_fails_does_not_stop_the_others_but_turns_the_run_red()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => category == Category.Mobiles
                ? throw new ScrapeParseException("layout changed")
                : Task.FromResult(Found(CrawlHarness.Scraped($"{category}1"))));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal((5, 1), (summary.CategoriesAttempted, summary.CategoriesFailed));
            Assert.Equal(4, summary.ProductsSucceeded);
            Assert.Equal(ExitCodes.Failure, summary.ExitCode(h.Options));

            var run = LastRun(h);
            Assert.Equal(JobStatus.Failed, run.Status);
            Assert.Equal(1, run.CategoriesFailed);
            Assert.Equal(4, h.Query(db => db.Products.Count()));
        }

        [Fact]
        public async Task Every_category_failing_is_still_a_completed_run_with_a_failure_exit_code()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, _ => throw new TimeoutException("blocked"));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(5, summary.CategoriesFailed);
            Assert.Equal(ExitCodes.Failure, summary.ExitCode(h.Options));
            Assert.Equal(JobStatus.Failed, LastRun(h).Status);
        }

        [Fact]
        public async Task A_transient_failure_is_retried_and_can_recover()
        {
            using var h = new CrawlHarness();
            var calls = 0;
            ServeCategories(h, category => category == Category.Mobiles && ++calls == 1
                ? throw new TimeoutException("slow")
                : Task.FromResult(Found()));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(0, summary.CategoriesFailed);
            Assert.Equal(1, h.Session.ResetCount);
        }

        [Fact]
        public async Task A_permanent_failure_is_not_retried()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => category == Category.Mobiles
                ? throw new ScrapeNavigationException(404, "u")
                : Task.FromResult(Found()));

            await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(1, h.Session.Calls.Count(c => c == $"category:{DailyCrawl.Categories[0].Url}"));
        }

        [Fact]
        public async Task The_page_is_reset_after_a_failed_category_so_it_cannot_poison_the_next()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => category == Category.Mobiles
                ? throw new ScrapeParseException("layout changed")
                : Task.FromResult(Found()));

            await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.True(h.Session.ResetCount >= 1);
        }
    }

    public class ProductFailures
    {
        [Fact]
        public async Task One_unrecordable_product_does_not_stop_the_rest()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => Task.FromResult(category == Category.Mobiles
                ? Found(CrawlHarness.Scraped("P1"), CrawlHarness.Scraped("BAD", price: 0), CrawlHarness.Scraped("P3"))
                : Found()));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal((3, 2, 1), (summary.ProductsAttempted, summary.ProductsSucceeded, summary.ProductsFailed));
            Assert.Equal(["P1", "P3"], h.Query(db => db.Products.OrderBy(p => p.Id).Select(p => p.NoonProductId!).ToList()));
        }

        [Fact]
        public async Task A_stray_failure_among_many_does_not_turn_the_run_red()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => Task.FromResult(category == Category.Mobiles
                ? Found(Enumerable.Range(1, 19).Select(i => CrawlHarness.Scraped($"OK{i}"))
                    .Append(CrawlHarness.Scraped("BAD", price: 0)).ToArray())
                : Found()));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal((20, 1), (summary.ProductsAttempted, summary.ProductsFailed));
            Assert.Equal(ExitCodes.Success, summary.ExitCode(h.Options));
        }

        [Fact]
        public async Task A_high_failure_rate_turns_the_run_red()
        {
            using var h = new CrawlHarness();
            ServeCategories(h, category => Task.FromResult(category == Category.Mobiles
                ? Found(CrawlHarness.Scraped("OK1"), CrawlHarness.Scraped("OK2"), CrawlHarness.Scraped("OK3"),
                        CrawlHarness.Scraped("B1", 0), CrawlHarness.Scraped("B2", 0))
                : Found()));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(0.4, summary.FailureRate, 3);
            Assert.Equal(ExitCodes.Failure, summary.ExitCode(h.Options));
        }

        // A failed database write leaves the shared context holding half-built
        // entities. If they leaked into the next product's save, every product after
        // the first failure would fail too - one bad row would poison the whole batch.
        [Fact]
        public async Task A_failed_database_write_does_not_poison_the_products_after_it()
        {
            using var h = new CrawlHarness();
            var today = h.Clock.GetUtcNow();
            var history = TestDb.AddProduct(h.Db, CrawlHarness.Scraped("FLAGGED").Url, "Flagged", source: ProductSource.Seed);
            TestDb.AddSnapshot(h.Db, history, 100, crawledAt: today.AddDays(-2));
            TestDb.AddSnapshot(h.Db, history, 150, crawledAt: today.AddDays(-1));
            h.Db.Database.ExecuteSqlRaw("""
                CREATE FUNCTION refuse_flags() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'flag insert refused'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER refuse_flags BEFORE INSERT ON "DiscountFlags"
                FOR EACH ROW EXECUTE FUNCTION refuse_flags();
                """);
            ServeCategories(h, category => Task.FromResult(category == Category.Mobiles
                ? Found(
                    CrawlHarness.Scraped("FLAGGED", 140, discount: 30),   // its flag insert is refused
                    CrawlHarness.Scraped("AFTER1"),
                    CrawlHarness.Scraped("AFTER2"))
                : Found()));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal((3, 2, 1), (summary.ProductsAttempted, summary.ProductsSucceeded, summary.ProductsFailed));
            Assert.Equal(2, h.Query(db => db.Products.Count(p => p.NoonProductId != null && p.NoonProductId.StartsWith("AFTER"))));
            // The failed product wrote nothing: still exactly its two prior snapshots.
            Assert.Equal(2, h.Query(db => db.PriceSnapshots.Count(s => s.ProductId == history.Id)));
        }

        [Fact]
        public async Task A_user_added_product_that_fails_is_isolated_and_the_page_is_reset()
        {
            using var h = new CrawlHarness();
            h.AddProduct("U1");
            h.AddProduct("U2");
            h.AddProduct("U3");
            h.Session.Product = (url, _) => NoonUrl.ExtractSku(url) == "U2"
                ? throw new ScrapeParseException("no price")
                : Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped(NoonUrl.ExtractSku(url)!));

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal((3, 2, 1), (summary.ProductsAttempted, summary.ProductsSucceeded, summary.ProductsFailed));
            Assert.True(h.Session.ResetCount >= 1);
        }

        [Fact]
        public async Task A_blocked_user_product_page_is_a_counted_failure_not_a_crash()
        {
            using var h = new CrawlHarness();
            h.AddProduct("U1");
            h.Session.Product = (_, _) => Task.FromResult<ScrapedProduct?>(null);

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(1, summary.ProductsFailed);
        }
    }

    public class TimeBudget
    {
        // Each product visit "takes" six minutes on the fake clock; the budget is ten.
        // The run must stop starting work, say how much it deferred, and have done the
        // products that had waited longest first.
        [Fact]
        public async Task A_run_out_of_time_defers_the_rest_oldest_first()
        {
            using var h = new CrawlHarness();
            h.Options.Budget = TimeSpan.FromMinutes(10);
            var now = h.Clock.GetUtcNow();
            var never = h.AddProduct("NEVER");
            var stale = h.AddProduct("STALE");
            var recent = h.AddProduct("RECENT");
            TestDb.AddSnapshot(h.Db, stale, 100, crawledAt: now.AddDays(-10));
            TestDb.AddSnapshot(h.Db, recent, 100, crawledAt: now.AddDays(-1));
            h.Session.Product = (url, _) =>
            {
                h.Clock.Advance(TimeSpan.FromMinutes(6));
                return Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped(NoonUrl.ExtractSku(url)!));
            };

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal((2, 1), (summary.ProductsAttempted, summary.ProductsDeferred));
            Assert.Equal(
                ["product:" + never.Url, "product:" + stale.Url],
                h.Session.Calls.Where(c => c.StartsWith("product:")).ToList());
            Assert.Equal(1, h.Query(db => db.PriceSnapshots.Count(s => s.ProductId == recent.Id)));
            Assert.Equal(1, LastRun(h).ProductsDeferred);
        }

        [Fact]
        public async Task A_run_with_time_to_spare_defers_nothing()
        {
            using var h = new CrawlHarness();
            h.AddProduct("U1");
            h.AddProduct("U2");
            ServeProducts(h);

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(0, summary.ProductsDeferred);
        }

        [Fact]
        public async Task Deferral_is_not_a_failure()
        {
            using var h = new CrawlHarness();
            h.Options.Budget = TimeSpan.FromMinutes(1);
            h.AddProduct("U1");
            h.AddProduct("U2");
            h.Session.Product = (url, _) =>
            {
                h.Clock.Advance(TimeSpan.FromMinutes(5));
                return Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped(NoonUrl.ExtractSku(url)!));
            };

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.True(summary.ProductsDeferred >= 1);
            Assert.Equal(ExitCodes.Success, summary.ExitCode(h.Options));
        }
    }

    public class Overlap
    {
        [Fact]
        public async Task A_second_run_while_one_is_active_exits_without_crawling()
        {
            using var h = new CrawlHarness();
            h.Db.CrawlRuns.Add(new CrawlRun { StartedAt = h.Clock.GetUtcNow().AddMinutes(-3) });
            h.Db.SaveChanges();
            h.AddProduct("U1");
            ServeProducts(h);

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.True(summary.SkippedBecauseAnotherRunIsActive);
            Assert.Equal(0, h.Sessions.OpenCount);
            Assert.Empty(h.Session.Calls);
            Assert.Equal(1, h.Query(db => db.CrawlRuns.Count()));
        }

        [Fact]
        public async Task Two_runs_started_together_are_one_run_and_one_skip()
        {
            using var h = new CrawlHarness();
            h.AddProduct("U1");
            h.Session.Product = async (url, _) =>
            {
                await Task.Delay(500);
                return CrawlHarness.Scraped(NoonUrl.ExtractSku(url)!);
            };

            var summaries = await Task.WhenAll(
                Task.Run(() => h.NewDailyCrawl().RunAsync(1, CancellationToken.None)),
                Task.Run(() => h.NewDailyCrawl().RunAsync(2, CancellationToken.None)));

            Assert.Equal(1, summaries.Count(s => s.SkippedBecauseAnotherRunIsActive));
            Assert.Equal(1, h.Session.CallsTo("product"));
            Assert.Equal(1, h.Query(db => db.CrawlRuns.Count()));
        }

        [Fact]
        public async Task A_finished_run_does_not_block_the_next()
        {
            using var h = new CrawlHarness();

            await h.NewDailyCrawl().RunAsync(1, CancellationToken.None);
            var second = await h.NewDailyCrawl().RunAsync(2, CancellationToken.None);

            Assert.False(second.SkippedBecauseAnotherRunIsActive);
            Assert.Equal(2, h.Query(db => db.CrawlRuns.Count()));
        }

        // A crashed run never got to mark itself finished. Left alone, its Running
        // row would block every future crawl.
        [Fact]
        public async Task A_run_that_died_long_ago_is_expired_and_does_not_block_forever()
        {
            using var h = new CrawlHarness();
            h.Db.CrawlRuns.Add(new CrawlRun { StartedAt = h.Clock.GetUtcNow().AddHours(-3) });
            h.Db.SaveChanges();

            var summary = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.False(summary.SkippedBecauseAnotherRunIsActive);
            var runs = h.Query(db => db.CrawlRuns.OrderBy(r => r.Id).ToList());
            Assert.Equal(2, runs.Count);
            Assert.Equal(JobStatus.Failed, runs[0].Status);
            Assert.Contains("Presumed dead", runs[0].Summary);
        }
    }

    public class Housekeeping
    {
        [Fact]
        public async Task Stale_requests_are_closed_at_the_start_of_every_run()
        {
            using var h = new CrawlHarness();
            var old = h.Clock.GetUtcNow().AddHours(-2);
            var check = h.AddCheckNow(h.AddProduct("A"), JobStatus.Pending, old);
            var crawl = h.AddCrawl(h.AddProduct("B"), JobStatus.Pending, old);
            var fresh = h.AddCheckNow(h.AddProduct("C"), JobStatus.Pending);

            await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.Equal(JobStatus.Failed, h.Query(db => db.CheckNowRequests.Single(r => r.Id == check.Id)).Status);
            Assert.Equal(JobStatus.Failed, h.Query(db => db.ProductCrawlRequests.Single(r => r.Id == crawl.Id)).Status);
            Assert.Equal(JobStatus.Pending, h.Query(db => db.CheckNowRequests.Single(r => r.Id == fresh.Id)).Status);
        }
    }

    public class RunLevelFailures
    {
        [Fact]
        public async Task A_browser_that_will_not_start_fails_the_run_loudly_and_records_it()
        {
            using var h = new CrawlHarness();
            h.Sessions.OpenFailure = new InvalidOperationException("no display");

            await Assert.ThrowsAsync<InvalidOperationException>(() => h.NewDailyCrawl().RunAsync(null, CancellationToken.None));

            var run = LastRun(h);
            Assert.Equal(JobStatus.Failed, run.Status);
            Assert.StartsWith("Run failed:", run.Summary);
            Assert.NotNull(run.CompletedAt);
        }

        [Fact]
        public async Task A_failed_run_does_not_leave_a_running_row_behind()
        {
            using var h = new CrawlHarness();
            h.Sessions.OpenFailure = new InvalidOperationException("no display");
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.NewDailyCrawl().RunAsync(null, CancellationToken.None));
            h.Sessions.OpenFailure = null;

            var next = await h.NewDailyCrawl().RunAsync(null, CancellationToken.None);

            Assert.False(next.SkippedBecauseAnotherRunIsActive);
        }
    }

    public class Cancellation
    {
        [Fact]
        public async Task A_cancelled_run_stops_promptly_reports_how_far_it_got_and_closes_its_row()
        {
            using var h = new CrawlHarness();
            using var cts = new CancellationTokenSource();
            var served = 0;
            ServeCategories(h, category =>
            {
                if (++served == 2)
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }

                return Task.FromResult(Found(CrawlHarness.Scraped($"{category}1")));
            });

            var summary = await h.NewDailyCrawl().RunAsync(7, cts.Token);

            Assert.True(summary.Cancelled);
            Assert.Equal(ExitCodes.Cancelled, summary.ExitCode(h.Options));
            Assert.Equal(1, summary.ProductsSucceeded);
            Assert.True(summary.CategoriesAttempted < 5);

            var run = LastRun(h);
            Assert.Equal(JobStatus.Failed, run.Status);
            Assert.Equal("Cancelled.", run.Summary);
            Assert.Equal(1, run.ProductsSucceeded);
        }

        [Fact]
        public async Task Products_recorded_before_cancellation_are_kept()
        {
            using var h = new CrawlHarness();
            using var cts = new CancellationTokenSource();
            h.AddProduct("U1");
            h.AddProduct("U2");
            h.AddProduct("U3");
            var visited = 0;
            h.Session.Product = (url, _) =>
            {
                if (++visited == 2)
                {
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }

                return Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped(NoonUrl.ExtractSku(url)!));
            };

            var summary = await h.NewDailyCrawl().RunAsync(null, cts.Token);

            Assert.True(summary.Cancelled);
            // Every product's reading is committed on its own, so what was done stays done.
            Assert.Equal(1, h.Query(db => db.PriceSnapshots.Count()));
            Assert.Equal(2, h.Session.CallsTo("product"));
        }
    }

    // The pause between page visits in the scheduled crawl. Every test here uses a
    // RecordingClock, which records each requested delay and moves time forward by it
    // instead of waiting, so nothing in this group sleeps.
    public class PoliteDelay
    {
        private static readonly DateTimeOffset T0 = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan Three = TimeSpan.FromSeconds(3);

        // Records, by the clock under test, when each page visit starts.
        private static List<DateTimeOffset> RecordVisits(CrawlHarness h, RecordingClock clock)
        {
            var starts = new List<DateTimeOffset>();
            h.Session.Category = (_, _) =>
            {
                lock (starts) { starts.Add(clock.GetUtcNow()); }
                return Task.FromResult(Found());
            };
            h.Session.Product = (url, _) =>
            {
                lock (starts) { starts.Add(clock.GetUtcNow()); }
                return Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped(NoonUrl.ExtractSku(url)!));
            };
            return starts;
        }

        [Fact]
        public async Task Waits_between_consecutive_visits_and_not_before_the_first()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            var clock = new RecordingClock(T0);
            h.AddProduct("U1");
            h.AddProduct("U2");
            var starts = RecordVisits(h, clock);
            var pausesBeforeFirstVisit = -1;
            var recordFirst = h.Session.Category;
            h.Session.Category = (url, ct) =>
            {
                if (pausesBeforeFirstVisit < 0)
                {
                    pausesBeforeFirstVisit = clock.Delays.Count;
                }

                return recordFirst(url, ct);
            };

            await h.NewDailyCrawl(clock: clock).RunAsync(null, CancellationToken.None);

            // 5 category pages and 2 product pages = 7 visits, so 6 pauses between them.
            Assert.Equal(7, starts.Count);
            Assert.Equal(0, pausesBeforeFirstVisit);
            Assert.Equal(6, clock.Delays.Count);
            Assert.All(clock.Delays, d => Assert.Equal(Three, d));
            Assert.Equal(T0, starts[0]);
            for (var i = 1; i < starts.Count; i++)
            {
                Assert.Equal(Three, starts[i] - starts[i - 1]);
            }
        }

        [Fact]
        public async Task A_failed_visit_still_counts_as_a_visit_for_pacing()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            var clock = new RecordingClock(T0);
            var starts = new List<DateTimeOffset>();
            h.Session.Category = (_, _) =>
            {
                starts.Add(clock.GetUtcNow());
                return starts.Count == 1 ? throw new ScrapeParseException("layout changed") : Task.FromResult(Found());
            };

            var summary = await h.NewDailyCrawl(clock: clock).RunAsync(null, CancellationToken.None);

            Assert.Equal(1, summary.CategoriesFailed);
            Assert.Equal(5, starts.Count);
            Assert.Equal(4, clock.Delays.Count);
            Assert.Equal(Three, starts[1] - starts[0]);
        }

        [Fact]
        public async Task A_product_already_recorded_from_a_category_is_not_a_visit_and_is_not_waited_for()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            var clock = new RecordingClock(T0);
            h.AddProduct("SHARED");
            var visits = RecordVisits(h, clock);
            var recordVisit = h.Session.Category;
            h.Session.Category = (url, ct) =>
            {
                recordVisit(url, ct);
                return Task.FromResult(Found(CrawlHarness.Scraped("SHARED")));
            };

            await h.NewDailyCrawl(clock: clock).RunAsync(null, CancellationToken.None);

            // SHARED was recorded from the first category, so its own page is never visited:
            // 5 visits, 4 pauses.
            Assert.Equal(5, visits.Count);
            Assert.Equal(4, clock.Delays.Count);
        }

        [Fact]
        public async Task A_single_product_crawl_is_not_slowed()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            var request = h.AddCrawl(h.AddProduct("U1"));
            h.Session.Product = (_, _) => Task.FromResult<ScrapedProduct?>(CrawlHarness.Scraped("U1"));

            // The harness clock is a FakeTimeProvider nobody advances: if the job waited on
            // it, this would hang, and WaitAsync turns that into a failure.
            var exit = await h.NewCrawlProductJob().RunAsync(request.Id, 1, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(1, h.Session.CallsTo("product"));
        }

        [Fact]
        public async Task A_single_check_now_is_not_slowed()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            var request = h.AddCheckNow(h.AddProduct("U1"));
            h.Session.Offers = (_, _) => Task.FromResult<IReadOnlyList<OfferResult>>(
                [new OfferResult { MerchantName = "noon", Price = 100 }]);

            var exit = await h.NewCheckNowJob().RunAsync(request.Id, 1, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(1, h.Session.CallsTo("offers"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task Zero_or_negative_delay_means_no_waiting(int seconds)
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = TimeSpan.FromSeconds(seconds);
            var clock = new RecordingClock(T0);
            h.AddProduct("U1");
            h.AddProduct("U2");
            var starts = RecordVisits(h, clock);

            await h.NewDailyCrawl(clock: clock).RunAsync(null, CancellationToken.None);

            Assert.Equal(7, starts.Count);
            Assert.Empty(clock.Delays);
            Assert.All(starts, t => Assert.Equal(T0, t));
        }

        [Fact]
        public async Task Cancelling_during_a_pause_stops_the_run_promptly()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            var clock = new RecordingClock(T0) { Hang = true };
            var starts = RecordVisits(h, clock);
            using var cts = new CancellationTokenSource();

            var run = h.NewDailyCrawl(clock: clock).RunAsync(7, cts.Token);
            // The run is now inside the pause that follows the first category page.
            await clock.WaitUntilHeldInDelay.WaitAsync(TimeSpan.FromSeconds(30));
            cts.Cancel();
            var summary = await run.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(summary.Cancelled);
            Assert.Equal(ExitCodes.Cancelled, summary.ExitCode(h.Options));
            Assert.Single(starts);
            Assert.Equal(1, summary.CategoriesAttempted);
            var saved = h.Query(db => db.CrawlRuns.AsNoTracking().Single());
            Assert.Equal(JobStatus.Failed, saved.Status);
            Assert.Equal("Cancelled.", saved.Summary);
        }

        [Fact]
        public async Task A_pause_that_would_cross_the_time_budget_is_not_taken_so_the_run_stops_starting_work()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            h.Options.Budget = TimeSpan.FromSeconds(5);
            var clock = new RecordingClock(T0);
            var starts = RecordVisits(h, clock);

            var summary = await h.NewDailyCrawl(clock: clock).RunAsync(null, CancellationToken.None);

            // Visit 1 at 0 s, a 3 s pause, visit 2 at 3 s; the next pause would end at 6 s,
            // past the 5 s budget, so it is not taken and no third page is visited.
            Assert.Equal(2, starts.Count);
            Assert.Equal([Three], clock.Delays);
            Assert.Equal(2, summary.CategoriesAttempted);
            Assert.Equal(0, summary.CategoriesFailed);
            Assert.False(summary.Cancelled);
            Assert.True(clock.GetUtcNow() < T0 + h.Options.Budget);
        }

        [Fact]
        public async Task When_the_pause_before_the_first_product_would_cross_the_budget_the_products_are_deferred()
        {
            using var h = new CrawlHarness();
            h.Options.DelayBetweenPages = Three;
            // Room for 4 pauses (12 s) between the 5 category pages, but not a 5th before products.
            h.Options.Budget = TimeSpan.FromSeconds(13);
            var clock = new RecordingClock(T0);
            h.AddProduct("U1");
            h.AddProduct("U2");
            var starts = RecordVisits(h, clock);

            var summary = await h.NewDailyCrawl(clock: clock).RunAsync(null, CancellationToken.None);

            Assert.Equal(5, starts.Count);
            Assert.Equal(4, clock.Delays.Count);
            Assert.Equal(2, summary.ProductsDeferred);
            Assert.Equal(0, h.Session.CallsTo("product"));
            Assert.Equal(ExitCodes.Success, summary.ExitCode(h.Options));
        }
    }

    public class PoliteDelayConfiguration
    {
        private static CrawlOptions Bind(params (string Key, string Value)[] settings) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
                .Build()
                .GetSection(CrawlOptions.SectionName)
                .Get<CrawlOptions>() ?? new CrawlOptions();

        [Fact]
        public void Unset_means_the_documented_default_of_three_seconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(3), Bind().DelayBetweenPages);
            Assert.Equal(TimeSpan.FromSeconds(3), new CrawlOptions().DelayBetweenPages);
        }

        [Theory]
        [InlineData("00:00:00", 0)]
        [InlineData("00:00:01.500", 1500)]
        [InlineData("00:00:10", 10000)]
        public void It_is_read_from_the_Crawl_section_as_a_time_span(string text, int expectedMilliseconds)
        {
            var options = Bind(("Crawl:DelayBetweenPages", text));

            Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), options.DelayBetweenPages);
        }

        [Fact]
        public void The_default_leaves_a_normal_run_well_inside_its_budget()
        {
            // 5 category pages + 11 user-added products (the size of the last recorded run)
            // = 16 visits = 15 pauses at most. That is a small share of the budget.
            var options = new CrawlOptions();

            var worstCasePauses = 15 * options.DelayBetweenPages;

            Assert.True(worstCasePauses < options.Budget / 10, $"{worstCasePauses} of {options.Budget}");
        }
    }

    public class Exit
    {
        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(100, 19, 0, 0)]
        [InlineData(100, 20, 0, 1)]
        [InlineData(100, 21, 0, 1)]
        [InlineData(5, 1, 0, 1)]
        [InlineData(1000, 199, 0, 0)]
        [InlineData(100, 0, 1, 1)]
        [InlineData(0, 0, 5, 1)]
        public void The_exit_code_follows_the_failure_policy(int attempted, int failed, int categoriesFailed, int expected)
        {
            var summary = new CrawlSummary(false, false, 5, categoriesFailed, attempted, attempted - failed, failed, 0);

            Assert.Equal(expected, summary.ExitCode(new CrawlOptions()));
        }

        [Fact]
        public void The_threshold_is_configurable()
        {
            var summary = new CrawlSummary(false, false, 5, 0, 100, 50, 50, 0);

            Assert.Equal(ExitCodes.Success, summary.ExitCode(new CrawlOptions { MaxFailureRate = 0.6 }));
            Assert.Equal(ExitCodes.Failure, summary.ExitCode(new CrawlOptions { MaxFailureRate = 0.5 }));
        }

        [Fact]
        public void Cancellation_has_its_own_exit_code_whatever_else_happened()
        {
            var summary = new CrawlSummary(false, true, 5, 5, 100, 0, 100, 0);

            Assert.Equal(ExitCodes.Cancelled, summary.ExitCode(new CrawlOptions()));
        }
    }
}
