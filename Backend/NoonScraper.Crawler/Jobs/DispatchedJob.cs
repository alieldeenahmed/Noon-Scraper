using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Crawler.Jobs;

public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;

    // 128 + SIGINT: the conventional "interrupted" code.
    public const int Cancelled = 130;
}

// Where in the job we were. Recorded on the request when it fails, so "why did
// this fail" is answered by the row (stage + message) rather than a log search.
public sealed class StageTracker
{
    public string Current { get; private set; } = Stages.Start;

    public void Enter(string stage) => Current = stage;
}

public static class Stages
{
    public const string Start = "start";
    public const string LaunchBrowser = "launch-browser";
    public const string Scrape = "scrape";
    public const string Persist = "persist";
    public const string Cancelled = "cancelled";
}

// The lifecycle every dispatched job shares - the on-demand crawl and the
// cross-merchant check:
//
//   load the request -> claim it (Pending -> Running, atomically) -> do the work
//   -> record the outcome (Running -> Completed | Failed).
//
// The properties that matter:
//  * Claiming is one conditional UPDATE, so if GitHub delivers the same request
//    twice (a re-run, a retried dispatch) only one worker proceeds; the other
//    exits successfully without scraping.
//  * Every exit path records an outcome. A failure is written to the request with
//    the stage it happened in and a message that is safe to show publicly; the
//    exception itself, with details, goes to the log.
//  * Outcome writes use a fresh cancellation token, so a cancelled job still
//    manages to say that it was cancelled.
//  * Completion is conditional on the request still being Running: if the API
//    expired it as stale while the worker was busy, the late result is discarded
//    instead of contradicting what everyone was already told.
public abstract class DispatchedJob<TRequest>(
    AppDbContext db,
    IScrapeSessionFactory sessions,
    TimeProvider clock,
    IOptions<CrawlOptions> options,
    ILogger logger)
    where TRequest : JobRequestBase
{
    protected AppDbContext Db => db;

    protected TimeProvider Clock => clock;

    protected CrawlOptions Options => options.Value;

    protected ILogger Logger => logger;

    protected abstract string JobName { get; }

    protected abstract DbSet<TRequest> Requests { get; }

    protected abstract Task<TRequest?> LoadAsync(int requestId, CancellationToken ct);

    // Does the work and completes the request. Returns whether the completion was
    // applied (false: the request was no longer Running, so the result was dropped).
    protected abstract Task<bool> ExecuteAsync(
        TRequest request, IScrapeSession session, StageTracker stage, CancellationToken ct);

    // A hook for job-specific consequences of a failure, after it has been
    // recorded on the request. Failures inside it are logged, never thrown.
    protected virtual Task OnFailedAsync(TRequest request, Exception failure) => Task.CompletedTask;

    public async Task<int> RunAsync(int requestId, long? gitHubRunId, CancellationToken ct)
    {
        var request = await LoadAsync(requestId, ct);
        if (request is null)
        {
            logger.LogError("{Job} request {RequestId} does not exist", JobName, requestId);
            return ExitCodes.Failure;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Job"] = JobName,
            ["RequestId"] = request.Id,
            ["ProductId"] = request.ProductId,
            ["CorrelationId"] = request.CorrelationId,
            ["GitHubRunId"] = gitHubRunId
        });

        if (!await JobLifecycle.TryClaimAsync(Requests, requestId, gitHubRunId, clock.GetUtcNow(), ct))
        {
            // Not an error: a duplicate delivery, or the request was expired or
            // failed before this worker started. Nothing to do.
            logger.LogInformation("Request is no longer pending (was {Status}); not running it again", request.Status);
            return ExitCodes.Success;
        }

        logger.LogInformation("Claimed request; starting");

        var stage = new StageTracker();
        try
        {
            stage.Enter(Stages.LaunchBrowser);
            await using var session = await sessions.OpenAsync(ct);

            var applied = await ExecuteAsync(request, session, stage, ct);
            if (applied)
            {
                logger.LogInformation("Completed");
            }
            else
            {
                logger.LogWarning("Work finished but the request had already been closed (expired?); result discarded");
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailAsync(requestId, Stages.Cancelled, new OperationCanceledException());
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            await FailAsync(requestId, stage.Current, ex);

            try
            {
                await OnFailedAsync(request, ex);
            }
            catch (Exception hookFailure)
            {
                logger.LogError(hookFailure, "Follow-up after the failure also failed");
            }

            return ExitCodes.Failure;
        }
    }

    protected async Task<bool> CompleteAsync(int requestId) =>
        await JobLifecycle.TryCompleteAsync(Requests, requestId, clock.GetUtcNow(), CancellationToken.None);

    protected Task<T> ScrapeWithRetryAsync<T>(Func<Task<T>> scrape, IScrapeSession session, CancellationToken ct) =>
        Retry.RunAsync(
            scrape,
            Options.MaxAttempts,
            Options.RetryDelay,
            clock,
            (ex, attempt) =>
            {
                logger.LogWarning("Scrape attempt {Attempt} failed ({Type}: {Message}); retrying with a fresh page",
                    attempt, ex.GetType().Name, ex.Message.Split('\n')[0]);
                return session.ResetAsync(ct);
            },
            ct);

    private async Task FailAsync(int requestId, string stage, Exception ex)
    {
        // Full detail to the log; only the sanitized description goes on the row.
        logger.LogError(ex, "Failed at stage {Stage}", stage);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var recorded = await JobLifecycle.TryFailAsync(
                Requests, requestId, stage, ScrapeErrors.Describe(ex), clock.GetUtcNow(), cts.Token);
            if (!recorded)
            {
                logger.LogWarning("Could not record the failure: the request was already closed");
            }
        }
        catch (Exception recordingFailure)
        {
            // The request will still be expired as stale; nothing more to do here.
            logger.LogError(recordingFailure, "Could not record the failure on the request");
        }
    }
}
