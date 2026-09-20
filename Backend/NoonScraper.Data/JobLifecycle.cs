using Microsoft.EntityFrameworkCore;
using NoonScraper.Data.Models;

namespace NoonScraper.Data;

// State transitions for dispatched jobs. Every transition is a single
// conditional UPDATE ("... WHERE Status = <expected>"), never read-modify-write,
// so concurrent actors - a duplicate workflow run, the API expiring a stale
// request, a worker finishing at the same moment - can't both win. The row count
// says who did.
public static class JobLifecycle
{
    public const int MaxErrorLength = 1000;

    // Worker picks the request up. False means somebody else already did (a
    // duplicate delivery), or it was expired/cancelled before this worker got
    // to it - the caller should stop, not run the job.
    public static async Task<bool> TryClaimAsync<T>(
        DbSet<T> requests, int id, long? gitHubRunId, DateTimeOffset now, CancellationToken ct = default)
        where T : JobRequestBase
    {
        var rows = await requests
            .Where(r => r.Id == id && r.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, JobStatus.Running)
                .SetProperty(r => r.StartedAt, (DateTimeOffset?)now)
                .SetProperty(r => r.GitHubRunId, gitHubRunId), ct);
        return rows == 1;
    }

    // Running -> Completed. False if the request is no longer Running (it was
    // expired while the worker was busy) - the result is then discarded rather
    // than resurrecting a request everyone has already been told failed.
    public static async Task<bool> TryCompleteAsync<T>(
        DbSet<T> requests, int id, DateTimeOffset now, CancellationToken ct = default)
        where T : JobRequestBase
    {
        var rows = await requests
            .Where(r => r.Id == id && r.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, JobStatus.Completed)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)now), ct);
        return rows == 1;
    }

    // Pending or Running -> Failed. Idempotent: failing an already-finished
    // request changes nothing and returns false.
    public static async Task<bool> TryFailAsync<T>(
        DbSet<T> requests, int id, string stage, string message, DateTimeOffset now, CancellationToken ct = default)
        where T : JobRequestBase
    {
        var truncated = Truncate(message);
        var rows = await requests
            .Where(r => r.Id == id && (r.Status == JobStatus.Pending || r.Status == JobStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, JobStatus.Failed)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)now)
                .SetProperty(r => r.FailureStage, stage)
                .SetProperty(r => r.ErrorMessage, truncated), ct);
        return rows == 1;
    }

    // Fails every active request nobody has touched for `staleAfter`: a Pending
    // one that no worker ever claimed (dispatch lost, workflow never started),
    // or a Running one whose worker died without reporting back (runner killed,
    // job timeout). Without this a dead request would sit in the table forever -
    // and, because at most one active request per product is allowed, block new
    // ones for that product.
    public static async Task<int> ExpireStaleAsync<T>(
        DbSet<T> requests, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct = default)
        where T : JobRequestBase
    {
        var cutoff = now - staleAfter;
        return await requests
            .Where(r =>
                (r.Status == JobStatus.Pending && r.RequestedAt < cutoff) ||
                (r.Status == JobStatus.Running && (r.StartedAt ?? r.RequestedAt) < cutoff))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ErrorMessage, r => r.Status == JobStatus.Pending
                    ? "Timed out: no worker picked this request up."
                    : "Timed out: the worker started this but never reported back.")
                .SetProperty(r => r.FailureStage, "timeout")
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)now)
                .SetProperty(r => r.Status, JobStatus.Failed), ct);
    }

    // What a reader should be told about a request right now. A request that is
    // past its deadline reads as failed even before anything has written that
    // down, so a GET never reports "still running" for something that isn't.
    public static JobView View(JobRequestBase request, DateTimeOffset now, TimeSpan staleAfter)
    {
        var cutoff = now - staleAfter;

        if (request.Status == JobStatus.Pending && request.RequestedAt < cutoff)
        {
            return new JobView(JobStatus.Failed, "timeout", "Timed out: no worker picked this request up.");
        }

        if (request.Status == JobStatus.Running && (request.StartedAt ?? request.RequestedAt) < cutoff)
        {
            return new JobView(JobStatus.Failed, "timeout", "Timed out: the worker started this but never reported back.");
        }

        return new JobView(request.Status, request.FailureStage, request.ErrorMessage);
    }

    public static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..(MaxErrorLength - 1)] + "…";
}

public readonly record struct JobView(JobStatus Status, string? FailureStage, string? ErrorMessage);
