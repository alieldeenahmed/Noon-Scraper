using System.Globalization;
using System.Text.Json;

namespace NoonScraper.Crawler;

// Reads Noon's schema.org Product JSON-LD block. Pure text-in / values-out - no
// browser involved - so every way the data can be missing or malformed is
// testable offline.
//
// Failure model: a page with no Product block at all returns null (the caller
// decides whether that's an anti-bot page or a bad URL); a Product block that is
// present but unusable throws ScrapeParseException with a message saying what's
// wrong. Nothing here ever guesses: a missing or non-positive price is an error,
// not a 0 - a fabricated 0 would be recorded as a price crash and could alert
// every subscriber of the product.
public static class ProductJsonLd
{
    public static ScrapedProduct? ParseProduct(IEnumerable<string> scriptTexts, string productUrl)
    {
        var product = FindProduct(scriptTexts);
        if (product is null)
        {
            return null;
        }

        var root = product.Value;

        var name = ReadString(root, "name")
            ?? throw new ScrapeParseException("Product data has no name.");
        var sku = ReadString(root, "sku")
            ?? throw new ScrapeParseException("Product data has no sku.");
        var offer = ReadOffer(root)
            ?? throw new ScrapeParseException("Product data has no offer.");
        var price = ReadPrice(offer)
            ?? throw new ScrapeParseException("Product data has no valid (positive) price.");

        return new ScrapedProduct
        {
            NoonProductId = sku,
            Url = productUrl,
            Name = name,
            Price = price,
            DiscountPercent = ReadDiscountPercent(offer, price),
            Rating = ReadRating(root),
            MerchantName = ReadSellerName(offer),
            Stock = ReadInStock(offer)
        };
    }

    // The single default offer, for products that have no "other sellers" panel.
    public static OfferResult? ParseDefaultOffer(IEnumerable<string> scriptTexts)
    {
        var product = FindProduct(scriptTexts);
        if (product is null)
        {
            return null;
        }

        var offer = ReadOffer(product.Value)
            ?? throw new ScrapeParseException("Product data has no offer.");
        var price = ReadPrice(offer)
            ?? throw new ScrapeParseException("Product data has no valid (positive) price.");

        return new OfferResult
        {
            MerchantName = ReadSellerName(offer) ?? "noon",
            Price = price
        };
    }

    // The first JSON-LD block that is (or, in an array or @graph, contains) a
    // Product. Blocks that aren't valid JSON are skipped: one broken block, such
    // as a third-party widget's, mustn't hide a good Product block next to it.
    private static JsonElement? FindProduct(IEnumerable<string> scriptTexts)
    {
        foreach (var text in scriptTexts)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                foreach (var candidate in Candidates(doc.RootElement))
                {
                    if (IsProduct(candidate))
                    {
                        // Clone: the element must outlive the document.
                        return candidate.Clone();
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Candidates(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;

            if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in graph.EnumerateArray())
                {
                    yield return item;
                }
            }
        }
    }

    private static bool IsProduct(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("@type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == "Product",
            JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "Product"),
            _ => false
        };
    }

    // "offers" is a single object on Noon's pages but schema.org allows a list;
    // the first offer object is used.
    private static JsonElement? ReadOffer(JsonElement product)
    {
        if (!product.TryGetProperty("offers", out var offers))
        {
            return null;
        }

        if (offers.ValueKind == JsonValueKind.Object)
        {
            return offers;
        }

        if (offers.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in offers.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    return item;
                }
            }
        }

        return null;
    }

    private static decimal? ReadPrice(JsonElement offer)
    {
        var price = ReadDecimal(offer, "price");
        return price is > 0 ? price : null;
    }

    // Noon's pre-discount price, when present, sits in priceSpecification.
    // A discount only exists if that price is actually higher than the current one.
    private static decimal? ReadDiscountPercent(JsonElement offer, decimal price)
    {
        if (!offer.TryGetProperty("priceSpecification", out var spec))
        {
            return null;
        }

        var specs = spec.ValueKind == JsonValueKind.Array ? spec.EnumerateArray().ToArray() : [spec];
        var wasPrice = specs
            .Where(s => s.ValueKind == JsonValueKind.Object)
            .Select(s => ReadDecimal(s, "price"))
            .Where(p => p is > 0)
            .DefaultIfEmpty(null)
            .Max();

        return wasPrice > price
            ? Math.Round((wasPrice.Value - price) / wasPrice.Value * 100, 0)
            : null;
    }

    // A rating outside (0, 5] is treated as absent rather than stored; 0 is how
    // "no ratings yet" tends to be written.
    private static decimal? ReadRating(JsonElement product)
    {
        if (!product.TryGetProperty("aggregateRating", out var aggregate) || aggregate.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rating = ReadDecimal(aggregate, "ratingValue");
        return rating is > 0 and <= 5 ? rating : null;
    }

    private static string? ReadSellerName(JsonElement offer) =>
        offer.TryGetProperty("seller", out var seller) && seller.ValueKind == JsonValueKind.Object
            ? ReadString(seller, "name")
            : null;

    // schema.org ItemAvailability, which Noon writes as a URL. Accepts either
    // scheme (http/https) by looking only at the last path segment. A missing or
    // unrecognized value counts as out of stock.
    private static bool ReadInStock(JsonElement offer)
    {
        var availability = ReadString(offer, "availability");
        if (availability is null)
        {
            return false;
        }

        var value = availability[(availability.LastIndexOf('/') + 1)..];
        return value is "InStock" or "LimitedAvailability" or "OnlineOnly";
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // schema.org allows a number or a numeric string for prices and ratings.
    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : null,
            JsonValueKind.String => decimal.TryParse(
                value.GetString(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null
        };
    }
}
