using NoonScraper.Data.Models;

namespace NoonScraper.Api.Dtos;

public class ProductDetailDto
{
    public required int Id { get; set; }

    public required string Url { get; set; }

    public string? NoonProductId { get; set; }

    public string? Name { get; set; }

    public Category? Category { get; set; }

    public string? MerchantName { get; set; }

    public decimal? Rating { get; set; }

    public required ProductSource Source { get; set; }

    public required bool IsActive { get; set; }

    public required DateTimeOffset AddedAt { get; set; }

    public decimal? LatestPrice { get; set; }

    public decimal? LatestDiscountPercent { get; set; }

    public bool? LatestStock { get; set; }

    public DateTimeOffset? LastCrawledAt { get; set; }
}
