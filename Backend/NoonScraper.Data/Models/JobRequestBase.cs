namespace NoonScraper.Data.Models;

// The bookkeeping every dispatched job has. Not itself an entity: each concrete
// request type is mapped to its own table and simply inherits these columns.
public abstract class JobRequestBase
{
    public int Id { get; set; }

    public required int ProductId { get; set; }

    public Product? Product { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public required DateTimeOffset RequestedAt { get; set; }

    // Set when a worker claims the request (Pending -> Running).
    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    // Where a failure happened (dispatch, launch-browser, scrape, persist,
    // timeout, ...), so "why did this fail" doesn't need a log search.
    public string? FailureStage { get; set; }

    // Generated when the request is created, sent in the GitHub dispatch payload,
    // shown in the workflow run's name, and attached to every log line on both
    // sides - the one id that ties an API request to a specific Actions run.
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    // GITHUB_RUN_ID of the run that claimed the request.
    public long? GitHubRunId { get; set; }
}
