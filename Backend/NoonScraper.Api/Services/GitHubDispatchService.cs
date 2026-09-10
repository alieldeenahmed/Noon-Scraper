using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NoonScraper.Api.Services;

// Triggers GitHub Actions workflows via repository_dispatch events, since
// this API has no browser of its own to scrape with - that's the Crawler's
// job, run via GitHub Actions rather than needing a persistent Chrome-capable
// host for the API itself.
public class GitHubDispatchService(HttpClient httpClient, IConfiguration configuration)
{
    private const string Owner = "alieldeenahmed";
    private const string Repo = "Noon-Scraper";

    // Triggers check-now.yml, which runs the cross-merchant offer comparison.
    public Task TriggerCheckNowAsync(int requestId) =>
        DispatchAsync("check-now", new { requestId });

    // Triggers crawl-product.yml, which fills in a freshly-submitted
    // product's own data (name, price, stock, category) - without this it
    // would just sit as a bare record until the next scheduled daily crawl,
    // up to 24 hours later.
    public Task TriggerCrawlProductAsync(int productId) =>
        DispatchAsync("crawl-product", new { productId });

    private async Task DispatchAsync(string eventType, object payload)
    {
        // Falls back to a flat variable name for the same reason the DB
        // connection string and Telegram config do - Back4app's
        // environment-variable UI rejects the "__"/":" hierarchical naming
        // .NET's standard config convention would otherwise need.
        var token = configuration["GitHubDispatch:Token"]
            ?? configuration["GITHUB_DISPATCH_TOKEN"]
            ?? throw new InvalidOperationException("GitHubDispatch:Token is not configured.");

        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{Owner}/{Repo}/dispatches")
        {
            Content = JsonContent.Create(new
            {
                event_type = eventType,
                client_payload = payload
            })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NoonScraper", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
