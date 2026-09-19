using System.Net;
using System.Net.Http.Json;
using NoonScraper.Api.Dtos;

namespace NoonScraper.Tests;

public class RateLimitingTests
{
    private static Task<HttpResponseMessage> Submit(HttpClient client, int n) =>
        client.PostAsJsonAsync("/api/products", new CreateProductRequestDto { Url = $"https://www.noon.com/egypt-en/p{n}/N{n}/p/" });

    private static Dictionary<string, string?> Limits(int global = 1000, int dispatch = 1000, int dispatchTotal = 1000) => new()
    {
        ["RateLimiting:GlobalPerMinute"] = global.ToString(),
        ["RateLimiting:DispatchPerMinute"] = dispatch.ToString(),
        ["RateLimiting:DispatchTotalPerMinute"] = dispatchTotal.ToString()
    };

    [Fact]
    public async Task Dispatch_endpoints_are_limited_per_client_ip()
    {
        using var factory = new ApiFactory(Limits(dispatch: 2));
        var client = factory.CreateClientFrom("10.0.0.1");

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
        {
            statuses.Add((await Submit(client, i)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(2, factory.Dispatch.CrawledProductIds.Count);
    }

    [Fact]
    public async Task A_rejected_request_says_when_to_retry()
    {
        using var factory = new ApiFactory(Limits(dispatch: 1));
        var client = factory.CreateClientFrom("10.0.0.1");

        await Submit(client, 1);
        var rejected = await Submit(client, 2);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task Each_client_ip_gets_its_own_dispatch_bucket()
    {
        using var factory = new ApiFactory(Limits(dispatch: 1));
        var first = factory.CreateClientFrom("10.0.0.1");
        var second = factory.CreateClientFrom("10.0.0.2");

        Assert.Equal(HttpStatusCode.Created, (await Submit(first, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Submit(first, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Submit(second, 3)).StatusCode);
    }

    [Fact]
    public async Task The_combined_dispatch_cap_holds_no_matter_how_many_ips_are_claimed()
    {
        using var factory = new ApiFactory(Limits(dispatchTotal: 2));

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
        {
            statuses.Add((await Submit(factory.CreateClientFrom($"10.0.0.{i}"), i)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Check_now_shares_the_dispatch_limit()
    {
        using var factory = new ApiFactory(Limits(dispatch: 1));
        var client = factory.CreateClientFrom("10.0.0.1");
        int productId = 0;
        factory.Seed(db => productId = TestDb.AddProduct(db).Id);

        var first = await client.PostAsync($"/api/products/{productId}/check-now", null);
        var second = await client.PostAsync($"/api/products/{productId}/check-now", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Read_endpoints_do_not_consume_the_dispatch_budget()
    {
        using var factory = new ApiFactory(Limits(dispatch: 1, dispatchTotal: 1));
        var client = factory.CreateClientFrom("10.0.0.1");

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await Submit(client, 1)).StatusCode);
    }

    [Fact]
    public async Task All_requests_are_limited_per_ip_by_the_global_limit()
    {
        using var factory = new ApiFactory(Limits(global: 3));
        var client = factory.CreateClientFrom("10.0.0.1");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.GetAsync("/api/products")).StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
            statuses);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClientFrom("10.0.0.2").GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task A_429_still_carries_cors_headers_so_the_browser_can_read_it()
    {
        using var factory = new ApiFactory(Limits(global: 1));
        var client = factory.CreateClientFrom("10.0.0.1");

        static HttpRequestMessage FromTheFrontend()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
            request.Headers.Add("Origin", "http://localhost:5173");
            return request;
        }

        await client.SendAsync(FromTheFrontend());
        var rejected = await client.SendAsync(FromTheFrontend());

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("http://localhost:5173", rejected.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
