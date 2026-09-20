using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Api.Services;

public enum RequestDisposition
{
    // A new request was created (and dispatched, unless DispatchFailed).
    Created,

    // The product already has an active request; it's returned instead of starting
    // a second one. This is what makes a double-click, a client retry, or two
    // requests racing harmless.
    ExistingActive,

    // Over the hourly dispatch budget, and the caller can wait: recorded as a
    // failed request with stage "budget", not dispatched.
    BudgetDeferred,

    // Over the budget and the caller asked for something that can't be deferred.
    BudgetRejected
}

public sealed record RequestOutcome<T>(T? Request, RequestDisposition Disposition, bool DispatchFailed = false)
    where T : JobRequestBase;

public enum ProductLimit
{
    None,
    HourlyRate,
    TotalCap
}

// Creating work for GitHub Actions: crawl a product, or compare its sellers. The
// rules live here, once, rather than in each endpoint:
//   1. expire stale requests first, so a dead one can't block the product forever;
//   2. if the product already has an active request, return it (idempotent);
//   3. enforce the dispatch budget;
//   4. insert - the partial unique index is the real guard, so if two requests race
//      past step 2 the loser gets the winner's request, not an error;
//   5. dispatch; if that fails, the request is closed as Failed at stage "dispatch"
//      rather than left Pending for a worker that will never come.
public sealed class JobRequestService(
    AppDbContext db,
    IJobDispatcher dispatcher,
    TimeProvider clock,
    IOptions<JobOptions> options,
    ILogger<JobRequestService> logger)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public Task<RequestOutcome<ProductCrawlRequest>> RequestCrawlAsync(int productId, CancellationToken ct = default) =>
        RequestAsync(
            db.ProductCrawlRequests,
            productId,
            now => new ProductCrawlRequest { ProductId = productId, RequestedAt = now },
            dispatcher.DispatchCrawlProductAsync,
            deferWhenOverBudget: true,
            ct);

    public Task<RequestOutcome<CheckNowRequest>> RequestCheckAsync(int productId, CancellationToken ct = default) =>
        RequestAsync(
            db.CheckNowRequests,
            productId,
            now => new CheckNowRequest { ProductId = productId, RequestedAt = now },
            dispatcher.DispatchCheckNowAsync,
            deferWhenOverBudget: false,
            ct);

    // How many dispatches were requested in the last hour, across both kinds.
    // Requests recorded as budget-deferred don't count: they cost nothing.
    public async Task<int> RecentDispatchCountAsync(CancellationToken ct = default)
    {
        var since = clock.GetUtcNow() - Hour;
        var crawls = await db.ProductCrawlRequests.CountAsync(r => r.RequestedAt >= since && r.FailureStage != "budget", ct);
        var checks = await db.CheckNowRequests.CountAsync(r => r.RequestedAt >= since && r.FailureStage != "budget", ct);
        return crawls + checks;
    }

    // Bounds on how many products visitors can add: per hour, and in total.
    public async Task<ProductLimit> CheckProductLimitsAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var since = clock.GetUtcNow() - Hour;

        if (await db.Products.CountAsync(p => p.Source == ProductSource.UserAdded && p.AddedAt >= since, ct) >= o.MaxNewProductsPerHour)
        {
            return ProductLimit.HourlyRate;
        }

        if (await db.Products.CountAsync(p => p.Source == ProductSource.UserAdded && p.IsActive, ct) >= o.MaxUserProducts)
        {
            return ProductLimit.TotalCap;
        }

        return ProductLimit.None;
    }

    private async Task<RequestOutcome<T>> RequestAsync<T>(
        DbSet<T> set,
        int productId,
        Func<DateTimeOffset, T> create,
        Func<T, CancellationToken, Task> dispatch,
        bool deferWhenOverBudget,
        CancellationToken ct)
        where T : JobRequestBase
    {
        var now = clock.GetUtcNow();
        await JobLifecycle.ExpireStaleAsync(set, now, options.Value.StaleAfter, ct);

        var existing = await FindActiveAsync(set, productId, ct);
        if (existing is not null)
        {
            return new RequestOutcome<T>(existing, RequestDisposition.ExistingActive);
        }

        var overBudget = await RecentDispatchCountAsync(ct) >= options.Value.MaxDispatchesPerHour;
        if (overBudget && !deferWhenOverBudget)
        {
            return new RequestOutcome<T>(null, RequestDisposition.BudgetRejected);
        }

        var request = create(now);
        if (overBudget)
        {
            request.Status = JobStatus.Failed;
            request.FailureStage = "budget";
            request.ErrorMessage = "Too many crawls were requested recently; the daily crawl will pick this product up.";
            request.CompletedAt = now;
        }

        set.Add(request);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_ProductCrawlRequests_ActivePerProduct" or "UX_CheckNowRequests_ActivePerProduct"
        })
        {
            // Lost a race to another request for the same product: use theirs.
            db.Entry(request).State = EntityState.Detached;
            var winner = await FindActiveAsync(set, productId, ct);
            return new RequestOutcome<T>(winner, RequestDisposition.ExistingActive);
        }

        if (overBudget)
        {
            logger.LogWarning("Dispatch budget exhausted; request {RequestId} for product {ProductId} was not dispatched", request.Id, productId);
            return new RequestOutcome<T>(request, RequestDisposition.BudgetDeferred);
        }

        try
        {
            await dispatch(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Dispatch failed for request {RequestId} (product {ProductId}, correlation {CorrelationId})",
                request.Id, productId, request.CorrelationId);

            var message = ex is DispatchException ? ex.Message : "The request could not be dispatched.";
            await JobLifecycle.TryFailAsync(set, request.Id, "dispatch", message, clock.GetUtcNow(), CancellationToken.None);

            var failed = await set.AsNoTracking().FirstAsync(r => r.Id == request.Id, ct);
            return new RequestOutcome<T>(failed, RequestDisposition.Created, DispatchFailed: true);
        }

        return new RequestOutcome<T>(request, RequestDisposition.Created);
    }

    private static Task<T?> FindActiveAsync<T>(DbSet<T> set, int productId, CancellationToken ct)
        where T : JobRequestBase =>
        set.AsNoTracking().FirstOrDefaultAsync(
            r => r.ProductId == productId && (r.Status == JobStatus.Pending || r.Status == JobStatus.Running), ct);
}
