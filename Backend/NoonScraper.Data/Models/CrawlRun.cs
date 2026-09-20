namespace NoonScraper.Data.Models;

// One execution of the scheduled crawl. Gives a durable, queryable record of
// what a run did (and a Running row that a second run can see and refuse to
// overlap with) without needing an external observability platform.
public class CrawlRun
{
    public int Id { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Running;

    public required DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long? GitHubRunId { get; set; }

    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    public int CategoriesAttempted { get; set; }

    public int CategoriesFailed { get; set; }

    public int ProductsAttempted { get; set; }

    public int ProductsSucceeded { get; set; }

    public int ProductsFailed { get; set; }

    // Products not reached because the run's time budget ran out. They are
    // picked up first next time (oldest-crawled first), so this drains.
    public int ProductsDeferred { get; set; }

    public string? Summary { get; set; }
}
