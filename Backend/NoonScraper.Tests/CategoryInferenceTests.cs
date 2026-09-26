using NoonScraper.Crawler;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

// A product a user pastes a link to has no listing page to take a category from, so
// it's worked out from the breadcrumb trail on its own page.
public class CategoryInferenceTests
{
    private static string[] Trail(params string[] crumbs) => crumbs;

    public class FromTheTrail
    {
        [Fact]
        public void The_real_charger_breadcrumb_is_mobiles()
        {
            var crumbs = ProductJsonLd.ReadBreadcrumb([Fixtures.Read("breadcrumb.jsonld.json")]);

            Assert.Equal(Category.Mobiles, CategoryInference.Infer(crumbs));
        }

        [Theory]
        [InlineData("Home", "Electronics & Mobiles", "Mobiles & Accessories", "Mobile Phones", "Smartphones")]
        [InlineData("Home", "Electronics & Mobiles", "Mobiles & Accessories", "Tablets")]
        public void Phones_and_tablets_are_mobiles(params string[] crumbs) =>
            Assert.Equal(Category.Mobiles, CategoryInference.Infer(crumbs));

        [Fact]
        public void Laptops_are_laptops_even_under_electronics()
        {
            var crumbs = Trail("Home", "Electronics & Mobiles", "Computers & Accessories", "Laptops");

            Assert.Equal(Category.Laptops, CategoryInference.Infer(crumbs));
        }

        [Fact]
        public void Skin_care_under_beauty_is_skin_care_not_personal_care()
        {
            var crumbs = Trail("Home", "Beauty", "Skin Care", "Cleansers");

            Assert.Equal(Category.SkinCare, CategoryInference.Infer(crumbs));
        }

        [Fact]
        public void Hair_care_is_hair_care()
        {
            var crumbs = Trail("Home", "Beauty", "Hair Care", "Shampoo");

            Assert.Equal(Category.HairCare, CategoryInference.Infer(crumbs));
        }

        [Theory]
        [InlineData("Home", "Beauty", "Fragrance", "Men's Perfume")]
        [InlineData("Home", "Beauty", "Personal Care", "Deodorants")]
        public void Fragrance_and_deodorant_are_personal_care(params string[] crumbs) =>
            Assert.Equal(Category.PersonalCare, CategoryInference.Infer(crumbs));

        [Fact]
        public void Headphones_are_electronics_not_mobiles_despite_the_parent_name()
        {
            // "Electronics & Mobiles" is the parent of both; only the deeper crumbs say which.
            var crumbs = Trail("Home", "Electronics & Mobiles", "Audio", "Headphones", "Over-Ear Headphones");

            Assert.Equal(Category.Electronics, CategoryInference.Infer(crumbs));
        }

        [Fact]
        public void The_most_specific_crumb_wins()
        {
            var crumbs = Trail("Home", "Beauty", "Skin Care", "Mobile Accessories");

            Assert.Equal(Category.Mobiles, CategoryInference.Infer(crumbs));
        }

        [Fact]
        public void A_trail_that_matches_nothing_is_other()
        {
            var crumbs = Trail("Home", "Home & Kitchen", "Drinkware", "Travel Mugs");

            Assert.Equal(Category.Other, CategoryInference.Infer(crumbs));
        }

        [Fact]
        public void Matching_looks_at_the_crumb_url_too()
        {
            var crumbs = Trail("Home https://www.noon.com/egypt-en/", "Care https://www.noon.com/egypt-en/eg-skin-care/");

            Assert.Equal(Category.SkinCare, CategoryInference.Infer(crumbs));
        }

        [Fact]
        public void Matching_ignores_case()
        {
            Assert.Equal(Category.Laptops, CategoryInference.Infer(Trail("HOME", "LAPTOPS")));
        }
    }

    public class WithNoTrail
    {
        [Fact]
        public void An_empty_trail_says_nothing()
        {
            Assert.Null(CategoryInference.Infer([]));
        }
    }

    public class ReadingTheBreadcrumb
    {
        [Fact]
        public void The_real_block_gives_one_entry_per_crumb_with_name_and_url()
        {
            var crumbs = ProductJsonLd.ReadBreadcrumb([Fixtures.Read("breadcrumb.jsonld.json")]);

            Assert.Equal(6, crumbs.Count);
            Assert.Equal("Home https://www.noon.com/egypt-en/", crumbs[0]);
            Assert.Contains("Wall Chargers", crumbs[^1]);
        }

        [Fact]
        public void A_product_page_carries_it_on_the_scraped_product()
        {
            var scripts = new[] { Fixtures.Read("product-anker.jsonld.json"), Fixtures.Read("breadcrumb.jsonld.json") };

            var product = ProductJsonLd.ParseProduct(scripts, "https://www.noon.com/egypt-en/x/N1/p/");

            Assert.Equal(6, product!.Breadcrumb.Count);
        }

        [Fact]
        public void A_page_without_one_has_an_empty_trail()
        {
            var product = ProductJsonLd.ParseProduct([Fixtures.Read("product-anker.jsonld.json")], "https://www.noon.com/egypt-en/x/N1/p/");

            Assert.Empty(product!.Breadcrumb);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{\"@type\":\"BreadcrumbList\"}")]
        [InlineData("{\"@type\":\"BreadcrumbList\",\"itemListElement\":\"nope\"}")]
        [InlineData("{\"@type\":\"BreadcrumbList\",\"itemListElement\":[1,2,3]}")]
        public void A_malformed_block_is_an_empty_trail_not_an_error(string block)
        {
            Assert.Empty(ProductJsonLd.ReadBreadcrumb([block]));
        }

        [Fact]
        public void A_broken_block_does_not_hide_a_good_one()
        {
            var crumbs = ProductJsonLd.ReadBreadcrumb(["not json", Fixtures.Read("breadcrumb.jsonld.json")]);

            Assert.Equal(6, crumbs.Count);
        }
    }

    public class WhenRecorded
    {
        private static ScrapedProduct WithTrail(string sku, params string[] trail)
        {
            var item = CrawlHarness.Scraped(sku);
            item.Breadcrumb = trail;
            return item;
        }

        private static Task Record(CrawlHarness h, ScrapedProduct item, Category? knownCategory = null, ProductSource source = ProductSource.UserAdded) =>
            h.NewRecorder().RecordAsync(item, source, knownCategory);

        [Fact]
        public async Task A_user_added_product_takes_the_category_its_page_points_to()
        {
            using var h = new CrawlHarness();

            await Record(h, WithTrail("U1", "Home", "Beauty", "Skin Care"));

            Assert.Equal(Category.SkinCare, h.Query(db => db.Products.Single().Category));
        }

        [Fact]
        public async Task A_category_the_caller_knows_wins_over_the_page()
        {
            using var h = new CrawlHarness();

            await Record(h, WithTrail("S1", "Home", "Beauty", "Skin Care"), Category.Laptops, ProductSource.Seed);

            Assert.Equal(Category.Laptops, h.Query(db => db.Products.Single().Category));
        }

        [Fact]
        public async Task An_existing_category_is_not_replaced_by_a_guess()
        {
            using var h = new CrawlHarness();
            await Record(h, WithTrail("U1", "Home", "Beauty", "Skin Care"));

            await Record(h, WithTrail("U1", "Home", "Electronics & Mobiles", "Laptops"));

            Assert.Equal(Category.SkinCare, h.Query(db => db.Products.Single().Category));
        }

        [Fact]
        public async Task A_product_already_uncategorized_gets_one_on_its_next_crawl()
        {
            using var h = new CrawlHarness();
            h.AddProduct("U1");

            await Record(h, WithTrail("U1", "Home", "Electronics & Mobiles", "Audio", "Headphones"));

            Assert.Equal(Category.Electronics, h.Query(db => db.Products.Single().Category));
        }

        [Fact]
        public async Task A_page_with_no_trail_leaves_the_category_alone()
        {
            using var h = new CrawlHarness();

            await Record(h, CrawlHarness.Scraped("U1"));

            Assert.Null(h.Query(db => db.Products.Single().Category));
        }
    }
}
