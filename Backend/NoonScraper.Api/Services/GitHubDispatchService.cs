using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NoonScraper.Api.Services;

// Triggers the check-now.yml GitHub Actions workflow via a repository_dispatch
// event, since this API has no browser of its own to perform the check -
// that's the Crawler's job, run once via GitHub Actions per request rather
// than needing a persistent Chrome-capable host for the API itself.
public class GitHubDispatchService(HttpClient httpClient, IConfiguration configuration)
{
    private const string Owner = "alieldeenahmed";
    private const string Repo = "Noon-Scraper";

    public async Task TriggerCheckNowAsync(int requestId)
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
                event_type = "check-now",
                client_payload = new { requestId }
            })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NoonScraper", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
