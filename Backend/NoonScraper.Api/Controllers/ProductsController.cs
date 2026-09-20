using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoonScraper.Api.Dtos;
using NoonScraper.Api.Services;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(
    AppDbContext db,
    JobRequestService jobs,
    TimeProvider clock,
    IOptions<JobOptions> jobOptions,
    ILogger<ProductsController> logger) : ControllerBase
{
    private const int MaxPageSize = 100;

    private const int MaxSearchLength = 100;

    // Search, sort, and paging all happen in the database rather than over one
    // big client-side list - the response is one page of the matching rows plus
    // the total, so the payload stays bounded as the tracked set grows.
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<ProductListItemDto>>> GetProducts(
        [FromQuery] Category? category,
        [FromQuery] string? search,
        [FromQuery] string sortBy = "crawled",
        [FromQuery] string sortDir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var descending = sortDir.ToLowerInvariant() switch
        {
            "desc" => true,
            "asc" => false,
            _ => (bool?)null
        };
        if (descending is null)
        {
            return Problem(detail: "sortDir must be 'asc' or 'desc'.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (sortBy.ToLowerInvariant() is not ("crawled" or "price" or "discount"))
        {
            return Problem(detail: "sortBy must be 'crawled', 'price', or 'discount'.", statusCode: StatusCodes.Status400BadRequest);
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var rows = ProductsWithLatestSnapshot();

        if (category is not null)
        {
            rows = rows.Where(x => x.Product.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Bounded: this becomes a substring match over every row.
            var trimmed = search.Trim();
            var term = (trimmed.Length > MaxSearchLength ? trimmed[..MaxSearchLength] : trimmed).ToLowerInvariant();
            rows = rows.Where(x => (x.Product.Name ?? x.Product.Url).ToLower().Contains(term));
        }

        var total = await rows.CountAsync();

        // Products with no value for the sorted field (never crawled, no
        // discount) always sort last, in either direction, rather than
        // wherever Postgres's NULL ordering would happen to put them.
        var ordered = (sortBy.ToLowerInvariant(), descending.Value) switch
        {
            ("price", true) => rows.OrderBy(x => x.Latest == null).ThenByDescending(x => x.Latest!.Price),
            ("price", false) => rows.OrderBy(x => x.Latest == null).ThenBy(x => x.Latest!.Price),
            ("discount", true) => rows.OrderBy(x => x.Latest == null || x.Latest.DiscountPercent == null)
                .ThenByDescending(x => x.Latest!.DiscountPercent),
            ("discount", false) => rows.OrderBy(x => x.Latest == null || x.Latest.DiscountPercent == null)
                .ThenBy(x => x.Latest!.DiscountPercent),
            (_, true) => rows.OrderBy(x => x.Latest == null).ThenByDescending(x => x.Latest!.CrawledAt),
            _ => rows.OrderBy(x => x.Latest == null).ThenBy(x => x.Latest!.CrawledAt)
        };

        var items = await ordered
            .ThenByDescending(x => x.Product.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ProductListItemDto
            {
                Id = x.Product.Id,
                Url = x.Product.Url,
                Name = x.Product.Name,
                Category = x.Product.Category,
                MerchantName = x.Product.MerchantName,
                Rating = x.Product.Rating,
                IsActive = x.Product.IsActive,
                LatestPrice = x.Latest != null ? x.Latest.Price : null,
                LatestDiscountPercent = x.Latest != null ? x.Latest.DiscountPercent : null,
                LatestStock = x.Latest != null ? x.Latest.Stock : null,
                LastCrawledAt = x.Latest != null ? x.Latest.CrawledAt : null
            })
            .ToListAsync();

        return Ok(new PagedResultDto<ProductListItemDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    // Headline counts for the list page. Needs its own endpoint now that the
    // list itself is paged - the client no longer holds every product to count.
    [HttpGet("stats")]
    public async Task<ActionResult<ProductStatsDto>> GetStats([FromQuery] Category? category)
    {
        var rows = ProductsWithLatestSnapshot();

        if (category is not null)
        {
            rows = rows.Where(x => x.Product.Category == category);
        }

        return Ok(new ProductStatsDto
        {
            Total = await rows.CountAsync(),
            InStock = await rows.CountAsync(x => x.Latest == null || x.Latest.Stock),
            OnDiscount = await rows.CountAsync(x => x.Latest != null && x.Latest.DiscountPercent != null)
        });
    }

    // Only tracked (active) products. A submitted product whose page turned out not
    // to exist is deactivated by its first crawl and stops appearing here.
    private IQueryable<ProductWithLatest> ProductsWithLatestSnapshot() =>
        db.Products
            .Where(p => p.IsActive)
            .Select(p => new ProductWithLatest
            {
                Product = p,
                Latest = p.PriceSnapshots
                    .OrderByDescending(s => s.CrawledAt)
                    .ThenByDescending(s => s.Id)
                    .FirstOrDefault()
            });

    private sealed class ProductWithLatest
    {
        public required Product Product { get; init; }

        public PriceSnapshot? Latest { get; init; }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductDetailDto>> GetProduct(int id, CancellationToken ct)
    {
        var product = await db.Products
            .Where(p => p.Id == id)
            .Select(p => new
            {
                Product = p,
                Latest = p.PriceSnapshots
                    .OrderByDescending(s => s.CrawledAt)
                    .ThenByDescending(s => s.Id)
                    .FirstOrDefault()
            })
            .Select(x => new ProductDetailDto
            {
                Id = x.Product.Id,
                Url = x.Product.Url,
                NoonProductId = x.Product.NoonProductId,
                Name = x.Product.Name,
                Category = x.Product.Category,
                MerchantName = x.Product.MerchantName,
                Rating = x.Product.Rating,
                Source = x.Product.Source,
                IsActive = x.Product.IsActive,
                AddedAt = x.Product.AddedAt,
                LatestPrice = x.Latest != null ? x.Latest.Price : null,
                LatestDiscountPercent = x.Latest != null ? x.Latest.DiscountPercent : null,
                LatestStock = x.Latest != null ? x.Latest.Stock : null,
                LastCrawledAt = x.Latest != null ? x.Latest.CrawledAt : null
            })
            .FirstOrDefaultAsync(ct);

        if (product is null)
        {
            return NotFound();
        }

        var latestCrawl = await db.ProductCrawlRequests
            .AsNoTracking()
            .Where(r => r.ProductId == id)
            .OrderByDescending(r => r.RequestedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

        product.Crawl = latestCrawl is null ? null : ToCrawlStatus(latestCrawl);

        return Ok(product);
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Dispatch)]
    public async Task<ActionResult<ProductDetailDto>> CreateProduct(CreateProductRequestDto request, CancellationToken ct)
    {
        // Strict, shared validation - see NoonUrl. Everything past this point works
        // with the canonical URL it builds, never the string the caller sent.
        if (!NoonUrl.TryParse(request.Url, out var noonUrl, out var error))
        {
            return Problem(title: "Invalid product URL", detail: error, statusCode: StatusCodes.Status400BadRequest);
        }

        var existingId = await FindExistingProductIdAsync(noonUrl, ct);
        if (existingId is not null)
        {
            return AlreadyTracked(existingId.Value);
        }

        switch (await jobs.CheckProductLimitsAsync(ct))
        {
            case ProductLimit.HourlyRate:
                Response.Headers.RetryAfter = "3600";
                return Problem(
                    title: "Too many new products",
                    detail: "New products are being added too quickly right now. Try again later.",
                    statusCode: StatusCodes.Status429TooManyRequests);

            case ProductLimit.TotalCap:
                return Problem(
                    title: "Tracking limit reached",
                    detail: "This site is tracking as many user-added products as it supports.",
                    statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Only the URL is known until the crawl fills in the rest.
        var product = new Product
        {
            Url = noonUrl.Canonical,
            NoonProductId = noonUrl.Sku,
            Source = ProductSource.UserAdded,
            IsActive = true
        };

        db.Products.Add(product);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DatabaseErrors.IsUniqueViolation(ex, "IX_Products_Url"))
        {
            // The check above and the insert aren't atomic; the unique index is what
            // actually guarantees one row per URL, so a concurrent submission of the
            // same product lands here and gets the same answer as a sequential one.
            db.Entry(product).State = EntityState.Detached;
            var winnerId = await db.Products.Where(p => p.Url == noonUrl.Canonical).Select(p => p.Id).FirstAsync(ct);
            return AlreadyTracked(winnerId);
        }

        // The product is saved either way. If the crawl can't be started (dispatch
        // failed, budget spent) that's recorded on its request, the response says
        // so, and the scheduled crawl still covers it.
        var outcome = await jobs.RequestCrawlAsync(product.Id, ct);

        var dto = new ProductDetailDto
        {
            Id = product.Id,
            Url = product.Url,
            NoonProductId = product.NoonProductId,
            Name = product.Name,
            Category = product.Category,
            MerchantName = product.MerchantName,
            Rating = product.Rating,
            Source = product.Source,
            IsActive = product.IsActive,
            AddedAt = product.AddedAt,
            Crawl = outcome.Request is null ? null : ToCrawlStatus(outcome.Request)
        };

        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, dto);
    }

    // The same URL, or the same product code in the same market under a different
    // spelling (e.g. the Arabic-language page of an item already tracked in English).
    private Task<int?> FindExistingProductIdAsync(NoonUrl url, CancellationToken ct)
    {
        var marketPrefix = $"https://www.noon.com/{url.Market}-";
        var skuSegment = $"/{url.Sku}/p/";

        return db.Products
            .Where(p => p.Url == url.Canonical || (p.Url.StartsWith(marketPrefix) && p.Url.Contains(skuSegment)))
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
    }

    private ActionResult AlreadyTracked(int productId) =>
        Conflict(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Already tracked",
            Detail = "This product is already tracked.",
            Extensions = { ["productId"] = productId }
        });

    [HttpGet("{id:int}/history")]
    public async Task<ActionResult<List<PriceSnapshotDto>>> GetProductHistory(int id)
    {
        var productExists = await db.Products.AnyAsync(p => p.Id == id);
        if (!productExists)
        {
            return NotFound();
        }

        var history = await db.PriceSnapshots
            .Where(s => s.ProductId == id)
            .OrderBy(s => s.CrawledAt)
            .ThenBy(s => s.Id)
            .Select(s => new PriceSnapshotDto
            {
                Price = s.Price,
                Stock = s.Stock,
                DiscountPercent = s.DiscountPercent,
                CrawledAt = s.CrawledAt
            })
            .ToListAsync();

        return Ok(history);
    }

    [HttpGet("{id:int}/discount-flags")]
    public async Task<ActionResult<List<DiscountFlagDto>>> GetDiscountFlags(int id)
    {
        var productExists = await db.Products.AnyAsync(p => p.Id == id);
        if (!productExists)
        {
            return NotFound();
        }

        var flags = await db.DiscountFlags
            .Where(f => f.ProductId == id)
            .OrderByDescending(f => f.DetectedAt)
            .ThenByDescending(f => f.Id)
            .Select(f => new DiscountFlagDto
            {
                PriorHighPrice = f.PriorHighPrice,
                PriorHighDetectedAt = f.PriorHighDetectedAt,
                HistoricalLowPrice = f.HistoricalLowPrice,
                DiscountedPrice = f.DiscountedPrice,
                DiscountPercent = f.DiscountPercent,
                DetectedAt = f.DetectedAt
            })
            .ToListAsync();

        return Ok(flags);
    }

    [HttpGet("{id:int}/restocks")]
    public async Task<ActionResult<List<RestockEventDto>>> GetRestockEvents(int id)
    {
        var productExists = await db.Products.AnyAsync(p => p.Id == id);
        if (!productExists)
        {
            return NotFound();
        }

        var restocks = await db.RestockEvents
            .Where(r => r.ProductId == id)
            .OrderByDescending(r => r.DetectedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new RestockEventDto
            {
                DetectedAt = r.DetectedAt
            })
            .ToListAsync();

        return Ok(restocks);
    }

    // On-demand cross-merchant comparison, done via GitHub Actions rather than
    // in-process - this API has no browser available to it, so it hands the
    // actual scrape off to the same Chrome-capable environment the daily crawl
    // runs in, then the caller polls for the result.
    //
    // Idempotent per product: while a check is already Pending or Running, asking
    // again returns that check instead of starting a second workflow run.
    [HttpPost("{id:int}/check-now")]
    [EnableRateLimiting(RateLimitPolicies.Dispatch)]
    public async Task<ActionResult<CheckNowAcceptedDto>> CheckNow(int id, CancellationToken ct)
    {
        var productExists = await db.Products.AnyAsync(p => p.Id == id, ct);
        if (!productExists)
        {
            return NotFound();
        }

        var outcome = await jobs.RequestCheckAsync(id, ct);

        if (outcome.Disposition == RequestDisposition.BudgetRejected)
        {
            Response.Headers.RetryAfter = "300";
            return Problem(
                title: "Too many checks",
                detail: "Too many live checks were requested recently. Try again in a few minutes.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (outcome.DispatchFailed)
        {
            return Problem(
                title: "Could not start the check",
                detail: outcome.Request?.ErrorMessage ?? "Failed to trigger the check.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var request = outcome.Request!;
        return AcceptedAtAction(nameof(GetCheckNowResult), new { id, requestId = request.Id }, new CheckNowAcceptedDto
        {
            RequestId = request.Id,
            Status = request.Status.ToString()
        });
    }

    [HttpGet("{id:int}/check-now/{requestId:int}")]
    public async Task<ActionResult<CheckNowResultDto>> GetCheckNowResult(int id, int requestId, CancellationToken ct)
    {
        var request = await db.CheckNowRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ProductId == id, ct);
        if (request is null)
        {
            return NotFound();
        }

        var view = JobLifecycle.View(request, clock.GetUtcNow(), jobOptions.Value.StaleAfter);

        List<OfferDto>? offers = null;
        decimal? lowestPrice = null;
        string? lowestMerchant = null;

        if (view.Status == JobStatus.Completed && request.ResultJson is not null)
        {
            try
            {
                // OfferResult (Crawler project) and OfferDto have identical shapes -
                // deserializing straight into the DTO avoids needing a project
                // reference between Api and Crawler just for this one type.
                offers = JsonSerializer.Deserialize<List<OfferDto>>(request.ResultJson) ?? [];
                if (offers.Count > 0)
                {
                    lowestPrice = offers[0].Price;
                    lowestMerchant = offers[0].MerchantName;
                }
            }
            catch (JsonException ex)
            {
                // Written by the crawler, so this means a bug or manual edit. Report
                // the check as failed rather than turning a poll into a 500.
                logger.LogError(ex, "Check-now request {RequestId} has an unreadable stored result", request.Id);
                view = new JobView(JobStatus.Failed, "result", "The stored result could not be read.");
                offers = null;
            }
        }

        return Ok(new CheckNowResultDto
        {
            RequestId = request.Id,
            Status = view.Status.ToString(),
            LowestPrice = lowestPrice,
            LowestPriceMerchant = lowestMerchant,
            Offers = offers,
            ErrorMessage = view.ErrorMessage,
            FailureStage = view.FailureStage,
            RequestedAt = request.RequestedAt,
            StartedAt = request.StartedAt,
            CompletedAt = request.CompletedAt,
            RunUrl = GitHubActions.RunUrl(request.GitHubRunId)
        });
    }

    private CrawlStatusDto ToCrawlStatus(ProductCrawlRequest request)
    {
        var view = JobLifecycle.View(request, clock.GetUtcNow(), jobOptions.Value.StaleAfter);
        return new CrawlStatusDto
        {
            RequestId = request.Id,
            Status = view.Status.ToString(),
            FailureStage = view.FailureStage,
            ErrorMessage = view.ErrorMessage,
            RequestedAt = request.RequestedAt,
            StartedAt = request.StartedAt,
            CompletedAt = request.CompletedAt,
            RunUrl = GitHubActions.RunUrl(request.GitHubRunId)
        };
    }
}
