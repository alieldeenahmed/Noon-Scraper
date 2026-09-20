using System.Text.Json.Nodes;
using NoonScraper.Crawler;

namespace NoonScraper.Tests;

// Offline, browser-free tests of the parsing that turns page text into numbers
// and structured data into products. The browser tests prove the selectors find
// the right elements on real markup; these prove what happens to whatever text
// and JSON turns up - including all the ways it can be missing or malformed.
public class PriceTextTests
{
    [Theory]
    [InlineData("1,299", 1299)]
    [InlineData("EGP 1,299", 1299)]
    [InlineData("12,999.50", 12999.5)]
    [InlineData("99", 99)]
    [InlineData("0", 0)]
    [InlineData("4.4", 4.4)]
    [InlineData("874.00", 874)]
    [InlineData("10301.95", 10301.95)]
    [InlineData("16% OFF", 16)]
    [InlineData("خصم 6%", 6)]
    [InlineData("-25%", 25)]
    [InlineData("  42  ", 42)]
    public void Reads_the_first_number_in_the_text(string text, double expected)
    {
        Assert.True(PriceText.TryParse(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("٥٠٠", 500)]
    [InlineData("١٢٬٩٩٩", 12999)]
    [InlineData("١٬٢٩٩٫٥٠", 1299.5)]
    [InlineData("۱۲۳", 123)]
    [InlineData("خصم ٦٪", 6)]
    public void Reads_arabic_digits_and_separators(string text, double expected)
    {
        Assert.True(PriceText.TryParse(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("free")]
    [InlineData("NEW")]
    [InlineData(",,,")]
    [InlineData(".")]
    [InlineData("EGP")]
    public void Text_without_a_number_does_not_parse(string? text)
    {
        Assert.False(PriceText.TryParse(text, out _));
        Assert.Null(PriceText.TryParseOrNull(text));
    }

    [Fact]
    public void Parse_throws_a_parse_exception_that_quotes_the_text()
    {
        var ex = Assert.Throws<ScrapeParseException>(() => PriceText.Parse("call us"));

        Assert.Contains("call us", ex.Message);
    }

    [Fact]
    public void A_very_long_unparseable_text_is_truncated_in_the_message()
    {
        var ex = Assert.Throws<ScrapeParseException>(() => PriceText.Parse(new string('x', 500)));

        Assert.True(ex.Message.Length < 120);
    }

    [Fact]
    public void An_absurdly_large_number_does_not_crash()
    {
        // Longer than a decimal can hold: reported as unparseable, not an OverflowException.
        Assert.False(PriceText.TryParse(new string('9', 60), out _));
    }
}

public class ProductJsonLdTests
{
    private const string Url = "https://www.noon.com/egypt-en/x/N1/p/";

    private static JsonNode Product() => Fixtures.ReadJson("product-anker.jsonld.json");

    private static ScrapedProduct? Parse(JsonNode json) => ProductJsonLd.ParseProduct([json.ToJsonString()], Url);

    private static ScrapedProduct ParseOk(JsonNode json) => Assert.IsType<ScrapedProduct>(Parse(json));

    private static string ParseError(JsonNode json) => Assert.Throws<ScrapeParseException>(() => Parse(json)).Message;

    public class RealData
    {
        [Fact]
        public void Reads_a_real_product()
        {
            var product = ParseOk(Product());

            Assert.Equal("N70294248V", product.NoonProductId);
            Assert.Equal(874m, product.Price);
            Assert.Equal(4.3m, product.Rating);
            Assert.Equal("Wi-Tech", product.MerchantName);
            Assert.True(product.Stock);
            Assert.Null(product.DiscountPercent);
            Assert.Equal(Url, product.Url);
        }
    }

    public class RequiredFields
    {
        [Fact]
        public void A_missing_name_is_an_error()
        {
            var json = Product();
            json.AsObject().Remove("name");

            Assert.Contains("no name", ParseError(json));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void A_blank_name_is_an_error(string name)
        {
            var json = Product();
            json["name"] = name;

            Assert.Contains("no name", ParseError(json));
        }

        [Fact]
        public void A_missing_sku_is_an_error()
        {
            var json = Product();
            json.AsObject().Remove("sku");

            Assert.Contains("no sku", ParseError(json));
        }

        [Fact]
        public void A_numeric_sku_is_read_as_text()
        {
            var json = Product();
            json["sku"] = 123456;

            Assert.Equal("123456", ParseOk(json).NoonProductId);
        }

        [Fact]
        public void Missing_offers_is_an_error()
        {
            var json = Product();
            json.AsObject().Remove("offers");

            Assert.Contains("no offer", ParseError(json));
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("\"not an offer\"")]
        [InlineData("42")]
        [InlineData("null")]
        [InlineData("[1, 2, 3]")]
        public void Offers_that_are_not_an_offer_are_an_error(string offers)
        {
            var json = Product();
            json["offers"] = JsonNode.Parse(offers);

            Assert.Contains("no offer", ParseError(json));
        }

        [Fact]
        public void The_first_offer_object_in_a_list_is_used()
        {
            var json = Product();
            var real = json["offers"]!.DeepClone();
            var other = json["offers"]!.DeepClone();
            other["price"] = 5;
            json["offers"] = new JsonArray("stray string", real, other);

            Assert.Equal(874m, ParseOk(json).Price);
        }
    }

    public class Price
    {
        [Fact]
        public void A_missing_price_is_an_error_not_a_zero()
        {
            var json = Product();
            json["offers"]!.AsObject().Remove("price");

            Assert.Contains("price", ParseError(json));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-874.5)]
        public void A_zero_or_negative_price_is_an_error(double price)
        {
            var json = Product();
            json["offers"]!["price"] = price;

            Assert.Contains("positive", ParseError(json));
        }

        [Theory]
        [InlineData("874.00", 874)]
        [InlineData("874", 874)]
        [InlineData(" 12.5 ", 12.5)]
        public void A_numeric_string_price_is_accepted(string price, double expected)
        {
            var json = Product();
            json["offers"]!["price"] = price;

            Assert.Equal((decimal)expected, ParseOk(json).Price);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData("1,299")]
        [InlineData("EGP 874")]
        public void An_unparseable_string_price_is_an_error(string price)
        {
            var json = Product();
            json["offers"]!["price"] = price;

            Assert.Contains("price", ParseError(json));
        }

        [Theory]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("[]")]
        public void A_price_of_the_wrong_json_type_is_an_error(string price)
        {
            var json = Product();
            json["offers"]!["price"] = JsonNode.Parse(price);

            Assert.Contains("price", ParseError(json));
        }
    }

    public class Discount
    {
        [Fact]
        public void Is_derived_from_the_pre_discount_price()
        {
            var json = Product();
            json["offers"]!["priceSpecification"] = new JsonObject { ["price"] = 1000 };

            Assert.Equal(13m, ParseOk(json).DiscountPercent);
        }

        [Fact]
        public void A_list_of_price_specifications_uses_the_highest()
        {
            var json = Product();
            json["offers"]!["priceSpecification"] = new JsonArray(
                new JsonObject { ["price"] = 900 }, new JsonObject { ["price"] = 1748 }, "junk");

            // (1748 - 874) / 1748 = 50%
            Assert.Equal(50m, ParseOk(json).DiscountPercent);
        }

        [Theory]
        [InlineData(874)]
        [InlineData(500)]
        [InlineData(0)]
        [InlineData(-10)]
        public void A_pre_discount_price_that_is_not_higher_is_no_discount(int listPrice)
        {
            var json = Product();
            json["offers"]!["priceSpecification"] = new JsonObject { ["price"] = listPrice };

            Assert.Null(ParseOk(json).DiscountPercent);
        }

        [Theory]
        [InlineData("\"text\"")]
        [InlineData("42")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"price\": \"soon\"}")]
        public void A_malformed_price_specification_is_ignored(string spec)
        {
            var json = Product();
            json["offers"]!["priceSpecification"] = JsonNode.Parse(spec);

            Assert.Null(ParseOk(json).DiscountPercent);
        }
    }

    public class Rating
    {
        [Theory]
        [InlineData(4.3, 4.3)]
        [InlineData(5, 5)]
        [InlineData(0.5, 0.5)]
        public void A_valid_rating_is_kept(double rating, double expected)
        {
            var json = Product();
            json["aggregateRating"]!["ratingValue"] = rating;

            Assert.Equal((decimal)expected, ParseOk(json).Rating);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(5.1)]
        [InlineData(87)]
        public void An_out_of_range_rating_is_treated_as_absent(double rating)
        {
            var json = Product();
            json["aggregateRating"]!["ratingValue"] = rating;

            Assert.Null(ParseOk(json).Rating);
        }

        [Fact]
        public void A_string_rating_is_accepted()
        {
            var json = Product();
            json["aggregateRating"]!["ratingValue"] = "4.5";

            Assert.Equal(4.5m, ParseOk(json).Rating);
        }

        [Theory]
        [InlineData("\"none\"")]
        [InlineData("[]")]
        [InlineData("null")]
        public void A_malformed_aggregate_rating_is_ignored(string aggregate)
        {
            var json = Product();
            json["aggregateRating"] = JsonNode.Parse(aggregate);

            Assert.Null(ParseOk(json).Rating);
        }

        [Fact]
        public void A_missing_rating_is_null()
        {
            var json = Product();
            json.AsObject().Remove("aggregateRating");

            Assert.Null(ParseOk(json).Rating);
        }
    }

    public class Stock
    {
        [Theory]
        [InlineData("https://schema.org/InStock", true)]
        [InlineData("http://schema.org/InStock", true)]
        [InlineData("InStock", true)]
        [InlineData("https://schema.org/LimitedAvailability", true)]
        [InlineData("https://schema.org/OnlineOnly", true)]
        [InlineData("https://schema.org/OutOfStock", false)]
        [InlineData("https://schema.org/SoldOut", false)]
        [InlineData("https://schema.org/PreOrder", false)]
        [InlineData("https://schema.org/BackOrder", false)]
        [InlineData("https://schema.org/Discontinued", false)]
        [InlineData("https://schema.org/InStoreOnly", false)]
        [InlineData("something else", false)]
        [InlineData("", false)]
        public void Is_read_from_the_availability_url(string availability, bool expected)
        {
            var json = Product();
            json["offers"]!["availability"] = availability;

            Assert.Equal(expected, ParseOk(json).Stock);
        }

        [Fact]
        public void Missing_availability_counts_as_out_of_stock()
        {
            var json = Product();
            json["offers"]!.AsObject().Remove("availability");

            Assert.False(ParseOk(json).Stock);
        }

        [Fact]
        public void Availability_of_the_wrong_type_counts_as_out_of_stock()
        {
            var json = Product();
            json["offers"]!["availability"] = new JsonArray("https://schema.org/InStock");

            Assert.False(ParseOk(json).Stock);
        }
    }

    public class Seller
    {
        [Fact]
        public void A_missing_seller_is_null()
        {
            var json = Product();
            json["offers"]!.AsObject().Remove("seller");

            Assert.Null(ParseOk(json).MerchantName);
        }

        [Theory]
        [InlineData("\"noon\"")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"name\": \"  \"}")]
        public void A_malformed_seller_is_null(string seller)
        {
            var json = Product();
            json["offers"]!["seller"] = JsonNode.Parse(seller);

            Assert.Null(ParseOk(json).MerchantName);
        }
    }

    public class FindingTheProductBlock
    {
        private static string Blocks(params string[] blocks) => string.Join("\n", blocks);

        private static ScrapedProduct? ParseBlocks(params string[] blocks) => ProductJsonLd.ParseProduct(blocks, Url);

        [Fact]
        public void No_blocks_at_all_is_null()
        {
            Assert.Null(ParseBlocks());
        }

        [Fact]
        public void Only_other_types_is_null()
        {
            Assert.Null(ParseBlocks(Fixtures.Read("breadcrumb.jsonld.json")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{ not json")]
        [InlineData("<html>")]
        [InlineData("null")]
        [InlineData("123")]
        [InlineData("\"a string\"")]
        [InlineData("[]")]
        [InlineData("[1, 2]")]
        [InlineData("{}")]
        public void Junk_blocks_are_skipped_without_crashing(string junk)
        {
            var product = ParseBlocks(junk, Product().ToJsonString());

            Assert.Equal("N70294248V", product!.NoonProductId);
        }

        [Fact]
        public void The_first_product_block_wins()
        {
            var first = Product();
            var second = Product();
            second["sku"] = "SECOND";

            Assert.Equal("N70294248V", ParseBlocks(first.ToJsonString(), second.ToJsonString())!.NoonProductId);
        }

        [Fact]
        public void A_product_inside_a_graph_is_found()
        {
            var graph = new JsonObject { ["@context"] = "https://schema.org", ["@graph"] = new JsonArray(new JsonObject { ["@type"] = "WebSite" }, Product()) };

            Assert.Equal("N70294248V", ParseBlocks(graph.ToJsonString())!.NoonProductId);
        }

        [Fact]
        public void A_product_inside_a_top_level_array_is_found()
        {
            var array = new JsonArray(new JsonObject { ["@type"] = "WebSite" }, Product());

            Assert.Equal("N70294248V", ParseBlocks(array.ToJsonString())!.NoonProductId);
        }

        [Fact]
        public void A_type_list_that_includes_product_counts()
        {
            var json = Product();
            json["@type"] = new JsonArray("Thing", "Product");

            Assert.Equal("N70294248V", ParseOk(json).NoonProductId);
        }

        [Theory]
        [InlineData("product")]
        [InlineData("PRODUCT")]
        [InlineData("ProductGroup")]
        public void The_type_is_matched_exactly_as_schema_org_writes_it(string type)
        {
            var json = Product();
            json["@type"] = type;

            Assert.Null(Parse(json));
        }

        [Fact]
        public void A_broken_block_before_a_good_one_does_not_hide_it()
        {
            // One third-party widget writing invalid JSON-LD mustn't take the product with it.
            Assert.NotNull(ParseBlocks("{\"@type\": \"Product\", ", Product().ToJsonString()));
        }

        [Fact]
        public void A_product_block_that_is_unusable_is_an_error_not_a_skip()
        {
            // Skipping it would let a later, worse block win - or report "no product
            // data", which reads as an anti-bot page rather than a markup change.
            var broken = Product();
            broken["offers"]!.AsObject().Remove("price");

            Assert.Throws<ScrapeParseException>(() => ParseBlocks(broken.ToJsonString(), Product().ToJsonString()));
        }
    }

    public class DefaultOffer
    {
        [Fact]
        public void Reads_the_seller_and_price()
        {
            var offer = ProductJsonLd.ParseDefaultOffer([Product().ToJsonString()]);

            Assert.Equal(("Wi-Tech", 874m), (offer!.MerchantName, offer.Price));
        }

        [Fact]
        public void A_missing_seller_is_attributed_to_noon()
        {
            var json = Product();
            json["offers"]!.AsObject().Remove("seller");

            Assert.Equal("noon", ProductJsonLd.ParseDefaultOffer([json.ToJsonString()])!.MerchantName);
        }

        [Fact]
        public void No_product_block_is_null()
        {
            Assert.Null(ProductJsonLd.ParseDefaultOffer([Fixtures.Read("breadcrumb.jsonld.json")]));
        }

        [Fact]
        public void An_invalid_price_is_an_error()
        {
            var json = Product();
            json["offers"]!["price"] = 0;

            Assert.Throws<ScrapeParseException>(() => ProductJsonLd.ParseDefaultOffer([json.ToJsonString()]));
        }
    }
}
