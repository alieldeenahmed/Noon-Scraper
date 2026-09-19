using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Dtos;
using NoonScraper.Api.Services;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(AppDbContext db, GitHubDispatchService dispatchService, ILogger<ProductsController> logger) : ControllerBase
{
    private const int MaxPageSize = 100;

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
            return BadRequest("sortDir must be 'asc' or 'desc'.");
        }

        if (sortBy.ToLowerInvariant() is not ("crawled" or "price" or "discount"))
        {
            return BadRequest("sortBy must be 'crawled', 'price', or 'discount'.");
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
            var term = search.Trim().ToLowerInvariant();
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

    private IQueryable<ProductWithLatest> ProductsWithLatestSnapshot() =>
        db.Products.Select(p => new ProductWithLatest
        {
            Product = p,
            Latest = p.PriceSnapshots
                .OrderByDescending(s => s.CrawledAt)
                .FirstOrDefault()
        });

    private sealed class ProductWithLatest
    {
        public required Product Product { get; init; }

        public PriceSnapshot? Latest { get; init; }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductDetailDto>> GetProduct(int id)
    {
        var product = await db.Products
            .Where(p => p.Id == id)
            .Select(p => new
            {
                Product = p,
                Latest = p.PriceSnapshots
                    .OrderByDescending(s => s.CrawledAt)
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
            .FirstOrDefaultAsync();

        if (product is null)
        {
            return NotFound();
        }

        return Ok(product);
    }

    // Rate-limited more tightly than the read endpoints: every accepted
    // request here fires a GitHub Actions dispatch, which is metered.
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Dispatch)]
    public async Task<ActionResult<ProductDetailDto>> CreateProduct(CreateProductRequestDto request)
    {
        // "noon.com" itself or a real subdomain - a bare EndsWith("noon.com")
        // would also accept lookalike hosts such as "evilnoon.com".
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            !(uri.Host.Equals("noon.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".noon.com", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest("Url must be an absolute noon.com product URL.");
        }

        // Strips Noon's per-session tracking query string, so the same product
        // submitted twice (with a different tracking token) is recognized as
        // already tracked instead of creating a duplicate.
        var normalizedUrl = UrlNormalizer.Normalize(request.Url);

        var alreadyTracked = await db.Products.AnyAsync(p => p.Url == normalizedUrl);
        if (alreadyTracked)
        {
            return Conflict("This product is already tracked.");
        }

        // Only the URL is known until the next crawl fills in the rest.
        var product = new Product
        {
            Url = normalizedUrl,
            Source = ProductSource.UserAdded,
            IsActive = true
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();

        // Best-effort - the product is already saved either way, and the
        // next scheduled daily crawl is a fallback if this dispatch fails,
        // so a GitHub API hiccup here shouldn't turn into a failed 201.
        try
        {
            await dispatchService.TriggerCrawlProductAsync(product.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispatch an immediate crawl for product {ProductId}", product.Id);
        }

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
            AddedAt = product.AddedAt
        };

        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, dto);
    }

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
            .Select(f => new DiscountFlagDto
            {
                PriorHighPrice = f.PriorHighPrice,
                PriorHighDetectedAt = f.PriorHighDetectedAt,
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
    [HttpPost("{id:int}/check-now")]
    [EnableRateLimiting(RateLimitPolicies.Dispatch)]
    public async Task<ActionResult<CheckNowAcceptedDto>> CheckNow(int id)
    {
        var productExists = await db.Products.AnyAsync(p => p.Id == id);
        if (!productExists)
        {
            return NotFound();
        }

        var request = new CheckNowRequest
        {
            ProductId = id,
            Status = CheckNowStatus.Pending,
            RequestedAt = DateTimeOffset.UtcNow
        };
        db.CheckNowRequests.Add(request);
        await db.SaveChangesAsync();

        try
        {
            await dispatchService.TriggerCheckNowAsync(request.Id);
        }
        catch (Exception ex)
        {
            request.Status = CheckNowStatus.Failed;
            request.ErrorMessage = $"Failed to trigger check: {ex.Message}";
            request.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return StatusCode(StatusCodes.Status502BadGateway, "Failed to trigger the check.");
        }

        return AcceptedAtAction(nameof(GetCheckNowResult), new { id, requestId = request.Id }, new CheckNowAcceptedDto
        {
            RequestId = request.Id,
            Status = request.Status.ToString()
        });
    }

    [HttpGet("{id:int}/check-now/{requestId:int}")]
    public async Task<ActionResult<CheckNowResultDto>> GetCheckNowResult(int id, int requestId)
    {
        var request = await db.CheckNowRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.ProductId == id);
        if (request is null)
        {
            return NotFound();
        }

        List<OfferDto>? offers = null;
        decimal? lowestPrice = null;
        string? lowestMerchant = null;

        if (request.Status == CheckNowStatus.Completed && request.ResultJson is not null)
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

        return Ok(new CheckNowResultDto
        {
            RequestId = request.Id,
            Status = request.Status.ToString(),
            LowestPrice = lowestPrice,
            LowestPriceMerchant = lowestMerchant,
            Offers = offers,
            ErrorMessage = request.ErrorMessage
        });
    }
}
