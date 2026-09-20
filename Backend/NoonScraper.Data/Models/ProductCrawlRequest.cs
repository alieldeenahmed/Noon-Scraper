namespace NoonScraper.Data.Models;

// One "crawl this product now" request, created when a user submits a URL.
// Without a row for it, a crawl that never happened (dispatch failed, workflow
// died) was indistinguishable from one still in progress.
public class ProductCrawlRequest : JobRequestBase
{
}
