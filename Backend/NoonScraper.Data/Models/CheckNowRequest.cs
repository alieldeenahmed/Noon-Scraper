namespace NoonScraper.Data.Models;

// Bridges the API (which requests a check) and the GitHub Actions workflow
// (which actually launches Chrome and performs it) - the API polls this row
// rather than launching a browser itself, since it no longer has one available.
public class CheckNowRequest
{
    public int Id { get; set; }

    public required int ProductId { get; set; }

    public Product? Product { get; set; }

    public required CheckNowStatus Status { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    // JSON-serialized List<OfferResult> - a temporary handoff value, not
    // something worth its own relational shape given it only exists briefly
    // between request and poll.
    public string? ResultJson { get; set; }

    public string? ErrorMessage { get; set; }
}
