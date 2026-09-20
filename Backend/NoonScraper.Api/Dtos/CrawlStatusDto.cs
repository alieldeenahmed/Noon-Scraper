namespace NoonScraper.Api.Dtos;

// Where a "crawl this product" request stands. Also what a failed one says about
// why: the stage it failed in and a message that is safe to show publicly.
public class CrawlStatusDto
{
    public required int RequestId { get; set; }

    // Pending, Running, Completed or Failed.
    public required string Status { get; set; }

    public string? FailureStage { get; set; }

    public string? ErrorMessage { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    // The GitHub Actions run that handled it, once one has claimed it.
    public string? RunUrl { get; set; }
}
