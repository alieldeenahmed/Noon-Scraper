using Microsoft.EntityFrameworkCore;
using NoonScraper.Data;
using NoonScraper.DemoData;

// Loads SYNTHETIC demo data (nothing here is scraped from noon.com) into an empty
// database, applying the migrations first.
//
//   dotnet run --project Backend/NoonScraper.DemoData
//   dotnet run --project Backend/NoonScraper.DemoData -- "<npgsql connection string>"
//
// The connection string comes from the first argument, else the environment
// (ConnectionStrings__DefaultConnection, or the flat DATABASE the hosting platform
// uses). With neither it falls back to the docker-compose database from
// Backend/.env.example, which is a local development default and nothing else.
//
// It refuses to run against a database that holds real products.
const string ComposeDefault = "Host=localhost;Port=5432;Database=noonscraper;Username=noon;Password=noon_dev_only";

var connectionString = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DATABASE")
    ?? ComposeDefault;

var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
Console.WriteLine($"Target: {builder.Host}:{builder.Port} / {builder.Database}");

await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

try
{
    Console.WriteLine("Applying migrations...");
    await db.Database.MigrateAsync();

    Console.WriteLine("Seeding synthetic demo data...");
    var result = await DemoSeeder.RunAsync(db, DateTimeOffset.UtcNow);

    Console.WriteLine(
        $"Done: {result.Products} products, {result.Snapshots} price snapshots, " +
        $"{result.RestockEvents} restock events, {result.DiscountFlags} discount flags " +
        "(flags and events come from the same rules the crawler uses).");
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Seeding failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
