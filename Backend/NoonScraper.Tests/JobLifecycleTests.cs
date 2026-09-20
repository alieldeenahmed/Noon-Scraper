using Microsoft.EntityFrameworkCore;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

public class JobLifecycleTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private static CheckNowRequest Read(CrawlHarness h, int id) =>
        h.Query(db => db.CheckNowRequests.AsNoTracking().Single(r => r.Id == id));

    public class Transitions
    {
        [Fact]
        public async Task A_pending_request_can_be_claimed_once()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());

            var first = await JobLifecycle.TryClaimAsync(h.Db.CheckNowRequests, request.Id, 12345, h.Clock.GetUtcNow());
            var second = await JobLifecycle.TryClaimAsync(h.Db.CheckNowRequests, request.Id, 999, h.Clock.GetUtcNow());

            Assert.True(first);
            Assert.False(second);
            var saved = Read(h, request.Id);
            Assert.Equal(JobStatus.Running, saved.Status);
            Assert.Equal(12345, saved.GitHubRunId);
            Assert.Equal(h.Clock.GetUtcNow(), saved.StartedAt);
        }

        // The whole point of claiming atomically: two workers handed the same
        // request race, and exactly one of them is told to run it.
        [Fact]
        public async Task Concurrent_claims_have_exactly_one_winner()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());

            var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                using var db = h.NewContext();
                return await JobLifecycle.TryClaimAsync(db.CheckNowRequests, request.Id, 1, h.Clock.GetUtcNow());
            })));

            Assert.Equal(1, claims.Count(claimed => claimed));
        }

        [Theory]
        [InlineData(JobStatus.Running)]
        [InlineData(JobStatus.Completed)]
        [InlineData(JobStatus.Failed)]
        public async Task Only_a_pending_request_can_be_claimed(JobStatus status)
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct(), status);

            Assert.False(await JobLifecycle.TryClaimAsync(h.Db.CheckNowRequests, request.Id, 1, h.Clock.GetUtcNow()));
        }

        [Fact]
        public async Task A_running_request_completes()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct(), JobStatus.Running);

            Assert.True(await JobLifecycle.TryCompleteAsync(h.Db.CheckNowRequests, request.Id, h.Clock.GetUtcNow()));

            var saved = Read(h, request.Id);
            Assert.Equal(JobStatus.Completed, saved.Status);
            Assert.Equal(h.Clock.GetUtcNow(), saved.CompletedAt);
        }

        [Theory]
        [InlineData(JobStatus.Pending)]
        [InlineData(JobStatus.Failed)]
        [InlineData(JobStatus.Completed)]
        public async Task Only_a_running_request_can_complete(JobStatus status)
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct(), status);

            Assert.False(await JobLifecycle.TryCompleteAsync(h.Db.CheckNowRequests, request.Id, h.Clock.GetUtcNow()));
            Assert.Equal(status, Read(h, request.Id).Status);
        }

        [Theory]
        [InlineData(JobStatus.Pending)]
        [InlineData(JobStatus.Running)]
        public async Task An_active_request_can_be_failed_with_a_stage_and_message(JobStatus status)
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct(), status);

            Assert.True(await JobLifecycle.TryFailAsync(h.Db.CheckNowRequests, request.Id, "scrape", "boom", h.Clock.GetUtcNow()));

            var saved = Read(h, request.Id);
            Assert.Equal(JobStatus.Failed, saved.Status);
            Assert.Equal("scrape", saved.FailureStage);
            Assert.Equal("boom", saved.ErrorMessage);
            Assert.NotNull(saved.CompletedAt);
        }

        [Theory]
        [InlineData(JobStatus.Completed)]
        [InlineData(JobStatus.Failed)]
        public async Task Failing_a_finished_request_changes_nothing(JobStatus status)
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct(), status);

            Assert.False(await JobLifecycle.TryFailAsync(h.Db.CheckNowRequests, request.Id, "scrape", "boom", h.Clock.GetUtcNow()));

            var saved = Read(h, request.Id);
            Assert.Equal(status, saved.Status);
            Assert.Null(saved.ErrorMessage);
        }

        [Fact]
        public async Task Overlong_error_messages_are_truncated_to_fit()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct());

            await JobLifecycle.TryFailAsync(h.Db.CheckNowRequests, request.Id, "scrape", new string('x', 5000), h.Clock.GetUtcNow());

            var saved = Read(h, request.Id);
            Assert.Equal(JobLifecycle.MaxErrorLength, saved.ErrorMessage!.Length);
        }

        [Fact]
        public async Task The_same_transitions_work_for_product_crawl_requests()
        {
            using var h = new CrawlHarness();
            var request = h.AddCrawl(h.AddProduct());

            Assert.True(await JobLifecycle.TryClaimAsync(h.Db.ProductCrawlRequests, request.Id, 7, h.Clock.GetUtcNow()));
            Assert.True(await JobLifecycle.TryCompleteAsync(h.Db.ProductCrawlRequests, request.Id, h.Clock.GetUtcNow()));

            Assert.Equal(JobStatus.Completed, h.Query(db => db.ProductCrawlRequests.Single().Status));
        }
    }

    public class Staleness
    {
        [Fact]
        public async Task A_pending_request_nobody_picked_up_is_expired()
        {
            using var h = new CrawlHarness();
            var request = h.AddCheckNow(h.AddProduct(), JobStatus.Pending, requestedAt: h.Clock.GetUtcNow() - StaleAfter - TimeSpan.FromSeconds(1));

            var expired = await JobLifecycle.ExpireStaleAsync(h.Db.CheckNowRequests, h.Clock.GetUtcNow(), StaleAfter);

            Assert.Equal(1, expired);
            var saved = Read(h, request.Id);
            Assert.Equal(JobStatus.Failed, saved.Status);
            Assert.Equal("timeout", saved.FailureStage);
            Assert.Contains("no worker picked", saved.ErrorMessage);
        }

        [Fact]
        public async Task A_running_request_whose_worker_never_reported_back_is_expired()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            var request = h.AddCheckNow(product, JobStatus.Running, requestedAt: h.Clock.GetUtcNow() - TimeSpan.FromHours(2));
            h.Db.CheckNowRequests.Where(r => r.Id == request.Id)
                .ExecuteUpdate(s => s.SetProperty(r => r.StartedAt, h.Clock.GetUtcNow() - StaleAfter - TimeSpan.FromMinutes(1)));

            await JobLifecycle.ExpireStaleAsync(h.Db.CheckNowRequests, h.Clock.GetUtcNow(), StaleAfter);

            var saved = Read(h, request.Id);
            Assert.Equal(JobStatus.Failed, saved.Status);
            Assert.Contains("never reported back", saved.ErrorMessage);
        }

        [Fact]
        public async Task A_running_request_is_judged_from_when_it_started_not_when_it_was_requested()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            // Requested an hour ago (sat in GitHub's queue), but only started a minute ago.
            var request = h.AddCheckNow(product, JobStatus.Running, requestedAt: h.Clock.GetUtcNow() - TimeSpan.FromHours(1));
            h.Db.CheckNowRequests.Where(r => r.Id == request.Id)
                .ExecuteUpdate(s => s.SetProperty(r => r.StartedAt, h.Clock.GetUtcNow() - TimeSpan.FromMinutes(1)));

            Assert.Equal(0, await JobLifecycle.ExpireStaleAsync(h.Db.CheckNowRequests, h.Clock.GetUtcNow(), StaleAfter));
            Assert.Equal(JobStatus.Running, Read(h, request.Id).Status);
        }

        [Fact]
        public async Task Fresh_and_finished_requests_are_left_alone()
        {
            using var h = new CrawlHarness();
            var old = h.Clock.GetUtcNow() - TimeSpan.FromDays(1);
            var fresh = h.AddCheckNow(h.AddProduct("N1"), JobStatus.Pending);
            var completed = h.AddCheckNow(h.AddProduct("N2"), JobStatus.Completed, requestedAt: old);
            var failed = h.AddCheckNow(h.AddProduct("N3"), JobStatus.Failed, requestedAt: old);

            Assert.Equal(0, await JobLifecycle.ExpireStaleAsync(h.Db.CheckNowRequests, h.Clock.GetUtcNow(), StaleAfter));

            Assert.Equal(JobStatus.Pending, Read(h, fresh.Id).Status);
            Assert.Equal(JobStatus.Completed, Read(h, completed.Id).Status);
            Assert.Null(Read(h, failed.Id).ErrorMessage);
        }

        [Fact]
        public async Task An_expired_request_frees_the_product_for_a_new_one()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            h.AddCheckNow(product, JobStatus.Pending, requestedAt: h.Clock.GetUtcNow() - TimeSpan.FromHours(1));

            // While the dead request counts as active, a new one is refused by the index.
            Assert.Throws<DbUpdateException>(() => h.AddCheckNow(product));
            h.Db.ChangeTracker.Clear();

            await JobLifecycle.ExpireStaleAsync(h.Db.CheckNowRequests, h.Clock.GetUtcNow(), StaleAfter);

            var replacement = h.AddCheckNow(product);
            Assert.True(replacement.Id > 0);
        }
    }

    public class ReaderView
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void A_stale_pending_request_reads_as_failed_before_anything_has_written_that_down()
        {
            var request = new CheckNowRequest { ProductId = 1, RequestedAt = Now - StaleAfter - TimeSpan.FromMinutes(1) };

            var view = JobLifecycle.View(request, Now, StaleAfter);

            Assert.Equal(JobStatus.Failed, view.Status);
            Assert.Equal("timeout", view.FailureStage);
        }

        [Fact]
        public void A_fresh_pending_request_reads_as_pending()
        {
            var request = new CheckNowRequest { ProductId = 1, RequestedAt = Now - TimeSpan.FromMinutes(1) };

            Assert.Equal(JobStatus.Pending, JobLifecycle.View(request, Now, StaleAfter).Status);
        }

        [Fact]
        public void A_stale_running_request_reads_as_failed()
        {
            var request = new CheckNowRequest
            {
                ProductId = 1,
                Status = JobStatus.Running,
                RequestedAt = Now - TimeSpan.FromHours(1),
                StartedAt = Now - StaleAfter - TimeSpan.FromMinutes(1)
            };

            Assert.Equal(JobStatus.Failed, JobLifecycle.View(request, Now, StaleAfter).Status);
        }

        [Fact]
        public void A_finished_request_reports_what_it_recorded()
        {
            var request = new CheckNowRequest
            {
                ProductId = 1,
                Status = JobStatus.Failed,
                RequestedAt = Now - TimeSpan.FromDays(1),
                FailureStage = "scrape",
                ErrorMessage = "No offers were found."
            };

            var view = JobLifecycle.View(request, Now, StaleAfter);

            Assert.Equal((JobStatus.Failed, "scrape", "No offers were found."), (view.Status, view.FailureStage, view.ErrorMessage));
        }

        [Fact]
        public void An_old_completed_request_stays_completed()
        {
            var request = new CheckNowRequest { ProductId = 1, Status = JobStatus.Completed, RequestedAt = Now - TimeSpan.FromDays(30) };

            Assert.Equal(JobStatus.Completed, JobLifecycle.View(request, Now, StaleAfter).Status);
        }
    }
}
