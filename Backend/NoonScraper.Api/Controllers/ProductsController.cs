using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Dtos;
using NoonScraper.Api.Services;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(AppDbContext db, GitHubDispatchService dispatchService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ProductListItemDto>>> GetProducts([FromQuery] Category? category)
    {
        var query = db.Products.AsQueryable();
        if (category is not null)
        {
            query = query.Where(p => p.Category == category);
        }

        var products = await query
            .OrderByDescending(p => p.AddedAt)
            .Select(p => new
            {
                Product = p,
                Latest = p.PriceSnapshots
                    .OrderByDescending(s => s.CrawledAt)
                    .FirstOrDefault()
            })
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

        return Ok(products);
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

    [HttpPost]
    public async Task<ActionResult<ProductDetailDto>> CreateProduct(CreateProductRequestDto request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("noon.com", StringComparison.OrdinalIgnoreCase))
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

    // On-demand cross-merchant comparison, done via GitHub Actions rather than
    // in-process - this API has no browser available to it, so it hands the
    // actual scrape off to the same Chrome-capable environment the daily crawl
    // runs in, then the caller polls for the result.
    [HttpPost("{id:int}/check-now")]
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
