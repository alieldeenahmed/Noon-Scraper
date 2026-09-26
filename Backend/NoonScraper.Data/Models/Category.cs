namespace NoonScraper.Data.Models;

public enum Category
{
    Mobiles,
    Laptops,
    SkinCare,
    HairCare,
    PersonalCare,

    // Products a user pastes a link to can be anything, so they need somewhere to
    // land besides the five listing pages the scheduled crawl reads.
    Electronics,
    Other
}
