namespace NoonScraper.Data.Models;

// Bridges the API (which requests a check) and the GitHub Actions workflow
// (which actually launches Chrome and performs it) - the API reads this row
// rather than launching a browser itself.
public class CheckNowRequest : JobRequestBase
{
    // JSON-serialized List<OfferResult> - a temporary handoff value, not
    // something worth its own relational shape given it only exists briefly
    // between request and poll.
    public string? ResultJson { get; set; }
}
