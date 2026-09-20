namespace NoonScraper.Api.Services;

// Limits on the work a public visitor can cause. Every dispatched job costs a
// GitHub Actions run, and every submitted product costs a browser visit on every
// scheduled crawl from then on, so both are bounded here - in the database, not in
// memory, so the limits survive container restarts and hold across instances, and
// don't depend on a client IP an attacker can forge.
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    // Workflow runs requested (crawl-on-submit and check-now together) per hour.
    public int MaxDispatchesPerHour { get; set; } = 30;

    // New user-submitted products per hour.
    public int MaxNewProductsPerHour { get; set; } = 20;

    // Active user-submitted products, total. Bounds the scheduled crawl's workload.
    public int MaxUserProducts { get; set; } = 500;

    // A Pending or Running request older than this is considered dead. The workflow
    // itself times out after 10 minutes, so 15 leaves room for GitHub's queueing.
    public int StaleAfterMinutes { get; set; } = 15;

    public TimeSpan StaleAfter => TimeSpan.FromMinutes(StaleAfterMinutes);
}
