using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NoonScraper.Api.Services;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

public class GitHubDispatchServiceTests
{
    private const string Token = "ghp_TESTTOKEN_do_not_leak";

    // Plays back a script of responses/failures and records what was sent.
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _index;

        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var step = script[Math.Min(_index++, script.Length - 1)];
            return step(request);
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(HttpStatusCode status, TimeSpan? retryAfter = null, string body = "{}") =>
        _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is not null)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
            }

            return response;
        };

    private static Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception ex) => _ => throw ex;

    private static (GitHubDispatchService Service, ScriptedHandler Handler, FakeTimeProvider Clock, CapturingLogger<GitHubDispatchService> Log) Build(
        string? token, params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    {
        var handler = new ScriptedHandler(script);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(token is null ? [] : new Dictionary<string, string?> { ["GITHUB_DISPATCH_TOKEN"] = token })
            .Build();
        var clock = new FakeTimeProvider();
        var log = new CapturingLogger<GitHubDispatchService>();
        return (new GitHubDispatchService(new HttpClient(handler), config, clock, log), handler, clock, log);
    }

    // The service backs off with Task.Delay on the fake clock; keep moving it so the
    // test doesn't wait in real time.
    private static async Task RunAsync(FakeTimeProvider clock, Func<Task> action)
    {
        var task = action();
        while (!task.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(1);
        }

        await task;
    }

    private static ProductCrawlRequest Crawl() => new()
    {
        Id = 42, ProductId = 7, RequestedAt = DateTimeOffset.UtcNow,
        CorrelationId = Guid.Parse("11111111-2222-3333-4444-555555555555")
    };

    public class Payload
    {
        [Fact]
        public async Task Sends_the_event_with_request_product_and_correlation_ids()
        {
            var (service, handler, _, _) = Build(Token, Respond(HttpStatusCode.NoContent));

            await service.DispatchCrawlProductAsync(Crawl());

            var (request, body) = Assert.Single(handler.Sent);
            Assert.Equal("https://api.github.com/repos/alieldeenahmed/Noon-Scraper/dispatches", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(("Bearer", Token), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
            Assert.Contains("NoonScraper", request.Headers.UserAgent.ToString());
            Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());

            using var json = JsonDocument.Parse(body);
            Assert.Equal("crawl-product", json.RootElement.GetProperty("event_type").GetString());
            var payload = json.RootElement.GetProperty("client_payload");
            Assert.Equal(42, payload.GetProperty("requestId").GetInt32());
            Assert.Equal(7, payload.GetProperty("productId").GetInt32());
            Assert.Equal("11111111-2222-3333-4444-555555555555", payload.GetProperty("correlationId").GetString());
        }

        [Fact]
        public async Task A_check_uses_its_own_event_type()
        {
            var (service, handler, _, _) = Build(Token, Respond(HttpStatusCode.NoContent));

            await service.DispatchCheckNowAsync(new CheckNowRequest { Id = 9, ProductId = 3, RequestedAt = DateTimeOffset.UtcNow });

            using var json = JsonDocument.Parse(handler.Sent.Single().Body);
            Assert.Equal("check-now", json.RootElement.GetProperty("event_type").GetString());
            Assert.Equal(9, json.RootElement.GetProperty("client_payload").GetProperty("requestId").GetInt32());
        }

        [Fact]
        public async Task The_dispatch_is_logged_with_the_ids_that_tie_it_to_a_workflow_run()
        {
            var (service, _, _, log) = Build(Token, Respond(HttpStatusCode.NoContent));

            await service.DispatchCrawlProductAsync(Crawl());

            var line = Assert.Single(log.Lines, l => l.Message.StartsWith("Dispatched"));
            Assert.Contains("42", line.Message);
            Assert.Contains("11111111-2222-3333-4444-555555555555", line.Message);
        }

        [Fact]
        public async Task The_token_is_never_logged()
        {
            var (service, _, clock, log) = Build(Token, Respond(HttpStatusCode.InternalServerError), Respond(HttpStatusCode.NoContent));

            await RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl()));

            Assert.DoesNotContain(log.Lines, l => l.Message.Contains(Token) || (l.Exception?.ToString().Contains(Token) ?? false));
        }
    }

    public class Retries
    {
        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        [InlineData(HttpStatusCode.RequestTimeout)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        public async Task A_transient_status_is_retried_and_can_then_succeed(HttpStatusCode transient)
        {
            var (service, handler, clock, _) = Build(Token, Respond(transient), Respond(HttpStatusCode.NoContent));

            await RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl()));

            Assert.Equal(2, handler.Sent.Count);
        }

        [Fact]
        public async Task A_network_error_is_retried()
        {
            var (service, handler, clock, _) = Build(
                Token, Throw(new HttpRequestException("connection reset")), Throw(new HttpRequestException("dns")), Respond(HttpStatusCode.NoContent));

            await RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl()));

            Assert.Equal(3, handler.Sent.Count);
        }

        [Fact]
        public async Task A_client_timeout_is_retried()
        {
            var (service, handler, clock, _) = Build(
                Token, Throw(new TaskCanceledException("timed out")), Respond(HttpStatusCode.NoContent));

            await RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl()));

            Assert.Equal(2, handler.Sent.Count);
        }

        [Fact]
        public async Task It_gives_up_after_three_attempts_with_a_safe_message()
        {
            var (service, handler, clock, _) = Build(Token, Respond(HttpStatusCode.ServiceUnavailable));

            var ex = await Assert.ThrowsAsync<DispatchException>(() => RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl())));

            Assert.Equal(3, handler.Sent.Count);
            Assert.Equal("GitHub rejected the dispatch (HTTP 503).", ex.Message);
        }

        [Fact]
        public async Task Persistent_network_failure_reports_that_github_was_unreachable()
        {
            var (service, handler, clock, _) = Build(Token, Throw(new HttpRequestException("secret host 10.1.2.3 refused")));

            var ex = await Assert.ThrowsAsync<DispatchException>(() => RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl())));

            Assert.Equal(3, handler.Sent.Count);
            Assert.Equal("Could not reach GitHub.", ex.Message);
            Assert.DoesNotContain("10.1.2.3", ex.Message);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.UnprocessableEntity)]
        [InlineData(HttpStatusCode.BadRequest)]
        public async Task A_permanent_failure_is_reported_at_once_without_retrying(HttpStatusCode permanent)
        {
            var (service, handler, clock, _) = Build(Token, Respond(permanent, body: "{\"message\":\"Bad credentials\"}"));

            var ex = await Assert.ThrowsAsync<DispatchException>(() => RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl())));

            Assert.Single(handler.Sent);
            Assert.Equal($"GitHub rejected the dispatch (HTTP {(int)permanent}).", ex.Message);
            Assert.DoesNotContain("Bad credentials", ex.Message);
        }

        [Fact]
        public async Task GitHubs_secondary_rate_limit_is_a_403_that_carries_retry_after_and_is_retried()
        {
            var (service, handler, clock, _) = Build(
                Token, Respond(HttpStatusCode.Forbidden, retryAfter: TimeSpan.FromSeconds(1)), Respond(HttpStatusCode.NoContent));

            await RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl()));

            Assert.Equal(2, handler.Sent.Count);
        }

        [Fact]
        public async Task A_very_long_retry_after_is_not_waited_out()
        {
            var (service, handler, clock, _) = Build(
                Token, Respond(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromHours(1)), Respond(HttpStatusCode.NoContent));

            // The wait is capped (a request is waiting on this), so a couple of
            // seconds of fake time is enough.
            await RunAsync(clock, () => service.DispatchCrawlProductAsync(Crawl()));

            Assert.Equal(2, handler.Sent.Count);
        }

        [Fact]
        public async Task The_caller_cancelling_stops_the_dispatch_and_is_not_swallowed()
        {
            var (service, handler, _, _) = Build(Token, Respond(HttpStatusCode.NoContent));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DispatchCrawlProductAsync(Crawl(), cts.Token));

            Assert.Empty(handler.Sent);
        }
    }

    public class Configuration
    {
        [Fact]
        public async Task With_no_token_nothing_is_sent_and_the_message_says_so()
        {
            var (service, handler, _, _) = Build(token: null, Respond(HttpStatusCode.NoContent));

            var ex = await Assert.ThrowsAsync<DispatchException>(() => service.DispatchCrawlProductAsync(Crawl()));

            Assert.Empty(handler.Sent);
            Assert.Contains("not configured", ex.Message);
        }

        [Fact]
        public async Task The_nested_config_key_works_as_well_as_the_flat_one()
        {
            var handler = new ScriptedHandler(Respond(HttpStatusCode.NoContent));
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHubDispatch:Token"] = "nested-token" })
                .Build();
            var service = new GitHubDispatchService(new HttpClient(handler), config, TimeProvider.System, NullLogger<GitHubDispatchService>.Instance);

            await service.DispatchCrawlProductAsync(Crawl());

            Assert.Equal("nested-token", handler.Sent.Single().Request.Headers.Authorization!.Parameter);
        }

        [Fact]
        public void The_run_url_points_at_the_actions_run_or_nothing()
        {
            Assert.Equal("https://github.com/alieldeenahmed/Noon-Scraper/actions/runs/123", GitHubActions.RunUrl(123));
            Assert.Null(GitHubActions.RunUrl(null));
        }
    }
}
