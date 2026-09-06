using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NoonScraper.Api.Dtos;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(AppDbContext db) : ControllerBase
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
}
