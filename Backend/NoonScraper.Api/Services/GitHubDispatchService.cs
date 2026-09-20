using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NoonScraper.Data.Configuration;
using NoonScraper.Data.Models;

namespace NoonScraper.Api.Services;

// What the API needs from "something that can start a browser-capable worker".
// Controllers and the request service depend on this, not on GitHub.
public interface IJobDispatcher
{
    Task DispatchCrawlProductAsync(ProductCrawlRequest request, CancellationToken ct = default);

    Task DispatchCheckNowAsync(CheckNowRequest request, CancellationToken ct = default);
}

// The dispatch could not be delivered. The message is written for users: it is
// stored on the request row and shown through the public API, so it never
// contains tokens, URLs, or response bodies (those go to the log).
public sealed class DispatchException(string publicMessage, Exception? inner = null)
    : Exception(publicMessage, inner);

public static class GitHubActions
{
    public const string Owner = "alieldeenahmed";
    public const string Repo = "Noon-Scraper";

    public static string? RunUrl(long? runId) =>
        runId is null ? null : $"https://github.com/{Owner}/{Repo}/actions/runs/{runId}";
}

// Starts crawl workers by sending GitHub Actions a repository_dispatch event.
// This API has no browser of its own; the workflow runs the crawler, which does.
//
// What the payload carries: the request's id and correlation id. The workflow
// shows both in the run's name and the crawler tags every log line with them, so
// "which run handled request 42" is answerable from either side.
//
// Reliability: a transient failure (network error, timeout, 5xx, rate limiting)
// is retried a couple of times with a short backoff, because losing the dispatch
// otherwise costs the user a full day (until the scheduled crawl). A permanent
// one (bad token, missing repo) is not retried - it's reported immediately.
// Retrying is safe: an active request is unique per product, and the worker
// claims a request atomically, so a dispatch delivered twice still runs once.
public sealed class GitHubDispatchService(
    HttpClient httpClient,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<GitHubDispatchService> logger) : IJobDispatcher
{
    private const int MaxAttempts = 3;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    public Task DispatchCrawlProductAsync(ProductCrawlRequest request, CancellationToken ct = default) =>
        DispatchAsync("crawl-product", request, ct);

    public Task DispatchCheckNowAsync(CheckNowRequest request, CancellationToken ct = default) =>
        DispatchAsync("check-now", request, ct);

    private async Task DispatchAsync(string eventType, JobRequestBase job, CancellationToken ct)
    {
        var token = configuration.GetGitHubDispatchToken();
        if (string.IsNullOrEmpty(token))
        {
            throw new DispatchException("GitHub dispatch is not configured on the server.");
        }

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://api.github.com/repos/{GitHubActions.Owner}/{GitHubActions.Repo}/dispatches")
            {
                Content = JsonContent.Create(new
                {
                    event_type = eventType,
                    client_payload = new { requestId = job.Id, productId = job.ProductId, correlationId = job.CorrelationId }
                })
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NoonScraper", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, ct);
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !ct.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    throw new DispatchException("Could not reach GitHub.", ex);
                }

                logger.LogWarning(
                    "Dispatch {EventType} for request {RequestId} attempt {Attempt} failed ({Type}); retrying",
                    eventType, job.Id, attempt, ex.GetType().Name);
                await Task.Delay(Backoff[attempt - 1], clock, ct);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Dispatched {EventType} for request {RequestId} (product {ProductId}, correlation {CorrelationId}) after {Attempts} attempt(s)",
                        eventType, job.Id, job.ProductId, job.CorrelationId, attempt);
                    return;
                }

                var status = response.StatusCode;
                var retryAfter = response.Headers.RetryAfter?.Delta;

                // 5xx, timeouts, and rate limiting can clear up. GitHub's secondary
                // rate limit arrives as a 403 carrying Retry-After; a 403 without one
                // is a real permission problem.
                var transient = (int)status >= 500
                    || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                    || (status == HttpStatusCode.Forbidden && retryAfter is not null);

                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "Dispatch {EventType} for request {RequestId} got HTTP {Status} on attempt {Attempt}: {Body}",
                    eventType, job.Id, (int)status, attempt, body.Length <= 300 ? body : body[..300]);

                if (!transient || attempt == MaxAttempts)
                {
                    throw new DispatchException($"GitHub rejected the dispatch (HTTP {(int)status}).");
                }

                var delay = retryAfter is { } wait && wait <= MaxRetryAfter ? wait : Backoff[attempt - 1];
                await Task.Delay(delay, clock, ct);
            }
        }
    }
}
