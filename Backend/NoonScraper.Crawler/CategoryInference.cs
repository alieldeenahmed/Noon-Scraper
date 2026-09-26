using NoonScraper.Data.Models;

namespace NoonScraper.Crawler;

// The category a product belongs to, worked out from the breadcrumb trail on its
// own page ("Home > Electronics & Mobiles > Mobiles & Accessories > Mobile
// Accessories > ..."). Products from the scheduled crawl get their category from
// the listing page they were found on; a product a user pastes a link to has no
// such page, so without this it would stay uncategorized forever.
//
// The trail is read from the most specific crumb up: "Mobile Accessories" under
// "Electronics & Mobiles" is Mobiles, whereas "Headphones" under the same parent
// matches nothing until the parent, and so is Electronics. A crumb is matched on
// both its name and its URL slug. Anything with a trail that matches no rule is
// Other; an empty trail says nothing, so it returns null and the product keeps
// whatever it had.
public static class CategoryInference
{
    // Checked in order for each crumb, so the more specific rule wins.
    private static readonly (Category Category, string[] Keywords)[] Rules =
    [
        (Category.Laptops, ["laptop", "notebook"]),
        (Category.SkinCare, ["skin care", "skin-care", "skincare"]),
        (Category.HairCare, ["hair care", "hair-care", "haircare", "shampoo", "conditioner", "hair styling"]),
        (Category.PersonalCare,
            ["personal care", "personal-care", "fragrance", "perfume", "deodorant", "grooming", "shaving", "bath & body",
             "bath-and-body", "oral care", "beauty"]),
        (Category.Mobiles, ["mobile phone", "mobile-phone", "smartphone", "mobiles-and-accessories", "mobiles & accessories",
                            "mobile accessories", "mobile-accessories", "/mobiles/", "tablet"]),
        (Category.Electronics, ["electronics", "audio", "headphone", "television", "camera", "gaming", "wearable", "smart watch"])
    ];

    public static Category? Infer(IReadOnlyList<string> breadcrumb)
    {
        if (breadcrumb.Count == 0)
        {
            return null;
        }

        for (var i = breadcrumb.Count - 1; i >= 0; i--)
        {
            var crumb = breadcrumb[i].ToLowerInvariant();
            foreach (var (category, keywords) in Rules)
            {
                if (keywords.Any(crumb.Contains))
                {
                    return category;
                }
            }
        }

        return Category.Other;
    }
}
