using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoonScraper.Data;
using NoonScraper.Data.Models;

namespace NoonScraper.Tests;

// The invariants that live in the database, tested against the database. None of
// these would mean anything on EF's in-memory provider, which ignores unique
// indexes, partial indexes and foreign keys.
public class DatabaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    public class Schema
    {
        [Fact]
        public void The_migrations_produce_exactly_the_model()
        {
            using var db = TestDb.Create();

            // If the model changed without a migration, deploying would drift from
            // what the code expects.
            Assert.False(db.Database.HasPendingModelChanges());
        }

        // The migration that adds the new constraints has to succeed on the data
        // production already has - including rows the old code could leave behind.
        [Fact]
        public async Task The_latest_migration_cleans_legacy_data_instead_of_failing_on_it()
        {
            var connectionString = await TestPostgres.CreateEmptyDatabaseAsync();
            await using (var db = TestDb.Open(connectionString))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260908143154_AddRestockEvent");

                // Legacy rows, written against the schema before the new constraints.
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Products" ("Url", "Source", "IsActive", "AddedAt") VALUES
                        ('https://www.noon.com/egypt-en/a/N1/p/', 1, true, now()),
                        ('https://www.noon.com/egypt-en/b/N2/p/', 1, true, now());
                    INSERT INTO "PriceSnapshots" ("ProductId", "Price", "Stock", "CrawledAt")
                        SELECT "Id", 100, true, now() FROM "Products";
                    -- two flags and two restocks for the same snapshot: redundant duplicates
                    INSERT INTO "DiscountFlags" ("ProductId", "TriggeringSnapshotId", "PriorHighPrice", "PriorHighDetectedAt", "DiscountedPrice", "DiscountPercent", "DetectedAt")
                        SELECT "ProductId", "Id", 150, now(), 140, 30, now() FROM "PriceSnapshots" WHERE "ProductId" = 1
                        UNION ALL
                        SELECT "ProductId", "Id", 150, now(), 140, 30, now() FROM "PriceSnapshots" WHERE "ProductId" = 1;
                    INSERT INTO "RestockEvents" ("ProductId", "TriggeringSnapshotId", "DetectedAt")
                        SELECT "ProductId", "Id", now() FROM "PriceSnapshots" WHERE "ProductId" = 1
                        UNION ALL
                        SELECT "ProductId", "Id", now() FROM "PriceSnapshots" WHERE "ProductId" = 1;
                    -- two still-pending checks for one product (would violate the new
                    -- one-active-per-product index), one with a huge error message
                    INSERT INTO "CheckNowRequests" ("ProductId", "Status", "RequestedAt", "ErrorMessage") VALUES
                        (1, 0, now(), NULL),
                        (1, 0, now(), NULL),
                        (2, 2, now(), repeat('x', 3000));
                    """);
            }

            await using var migrated = TestDb.Open(connectionString);
            await migrated.Database.MigrateAsync();

            Assert.Equal(1, await migrated.DiscountFlags.CountAsync());
            Assert.Equal(1, await migrated.RestockEvents.CountAsync());
            Assert.Equal(0, await migrated.CheckNowRequests.CountAsync(r => r.Status == JobStatus.Pending));
            Assert.Equal(2, await migrated.CheckNowRequests.CountAsync(r => r.Status == JobStatus.Failed && r.ProductId == 1));
            Assert.All(await migrated.CheckNowRequests.ToListAsync(), r => Assert.NotEqual(Guid.Empty, r.CorrelationId));
            Assert.Equal(1000, (await migrated.CheckNowRequests.SingleAsync(r => r.ProductId == 2)).ErrorMessage!.Length);
        }
    }

    public class Uniqueness
    {
        [Fact]
        public void A_product_url_is_unique()
        {
            using var db = TestDb.Create();
            TestDb.AddProduct(db, "https://www.noon.com/egypt-en/a/N1/p/");

            var ex = Assert.Throws<DbUpdateException>(() => TestDb.AddProduct(db, "https://www.noon.com/egypt-en/a/N1/p/"));

            Assert.True(DatabaseErrors.IsUniqueViolation(ex, "IX_Products_Url"));
        }

        [Fact]
        public void A_chat_can_subscribe_to_a_product_only_once()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            db.NotificationSubscriptions.Add(new NotificationSubscription { ProductId = product.Id, TelegramChatId = 1 });
            db.SaveChanges();

            db.NotificationSubscriptions.Add(new NotificationSubscription { ProductId = product.Id, TelegramChatId = 1 });

            Assert.True(DatabaseErrors.IsUniqueViolation(Assert.Throws<DbUpdateException>(() => db.SaveChanges())));
        }

        [Fact]
        public void A_snapshot_triggers_at_most_one_discount_flag()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            var snapshot = TestDb.AddSnapshot(db, product, 100);
            db.DiscountFlags.Add(NewFlag(product, snapshot));
            db.SaveChanges();

            db.DiscountFlags.Add(NewFlag(product, snapshot));

            Assert.True(DatabaseErrors.IsUniqueViolation(Assert.Throws<DbUpdateException>(() => db.SaveChanges())));
        }

        [Fact]
        public void A_snapshot_triggers_at_most_one_restock_event()
        {
            using var db = TestDb.Create();
            var product = TestDb.AddProduct(db);
            var snapshot = TestDb.AddSnapshot(db, product, 100);
            db.RestockEvents.Add(new RestockEvent { ProductId = product.Id, TriggeringSnapshotId = snapshot.Id });
            db.SaveChanges();

            db.RestockEvents.Add(new RestockEvent { ProductId = product.Id, TriggeringSnapshotId = snapshot.Id });

            Assert.True(DatabaseErrors.IsUniqueViolation(Assert.Throws<DbUpdateException>(() => db.SaveChanges())));
        }

        private static DiscountFlag NewFlag(Product product, PriceSnapshot snapshot) => new()
        {
            ProductId = product.Id,
            TriggeringSnapshotId = snapshot.Id,
            PriorHighPrice = 150,
            PriorHighDetectedAt = T0,
            DiscountedPrice = 140,
            DiscountPercent = 30
        };
    }

    public class ActiveJobs
    {
        [Theory]
        [InlineData(JobStatus.Pending, JobStatus.Pending)]
        [InlineData(JobStatus.Pending, JobStatus.Running)]
        [InlineData(JobStatus.Running, JobStatus.Pending)]
        [InlineData(JobStatus.Running, JobStatus.Running)]
        public void A_product_has_at_most_one_active_check(JobStatus existing, JobStatus attempted)
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            h.AddCheckNow(product, existing);

            var ex = Assert.Throws<DbUpdateException>(() => h.AddCheckNow(product, attempted));

            Assert.True(DatabaseErrors.IsUniqueViolation(ex, "UX_CheckNowRequests_ActivePerProduct"));
        }

        [Theory]
        [InlineData(JobStatus.Pending)]
        [InlineData(JobStatus.Running)]
        public void A_product_has_at_most_one_active_crawl(JobStatus existing)
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            h.AddCrawl(product, existing);

            var ex = Assert.Throws<DbUpdateException>(() => h.AddCrawl(product));

            Assert.True(DatabaseErrors.IsUniqueViolation(ex, "UX_ProductCrawlRequests_ActivePerProduct"));
        }

        [Theory]
        [InlineData(JobStatus.Completed)]
        [InlineData(JobStatus.Failed)]
        public void A_finished_request_does_not_block_a_new_one(JobStatus finished)
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            h.AddCheckNow(product, finished);
            h.AddCheckNow(product, finished);

            var next = h.AddCheckNow(product, JobStatus.Pending);

            Assert.True(next.Id > 0);
        }

        [Fact]
        public void Different_products_can_each_have_an_active_request()
        {
            using var h = new CrawlHarness();
            h.AddCheckNow(h.AddProduct("N1"));
            h.AddCheckNow(h.AddProduct("N2"));
            h.AddCrawl(h.AddProduct("N3"));
            h.AddCrawl(h.AddProduct("N4"));
        }

        [Fact]
        public async Task Concurrent_inserts_of_an_active_request_leave_exactly_one()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();

            var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                using var db = h.NewContext();
                db.CheckNowRequests.Add(new CheckNowRequest { ProductId = product.Id, RequestedAt = T0 });
                try
                {
                    db.SaveChanges();
                    return true;
                }
                catch (DbUpdateException ex) when (DatabaseErrors.IsUniqueViolation(ex))
                {
                    return false;
                }
            })));

            Assert.Equal(1, outcomes.Count(inserted => inserted));
            Assert.Equal(1, h.Query(db => db.CheckNowRequests.Count()));
        }

        [Fact]
        public void Only_one_scheduled_crawl_can_be_running()
        {
            using var h = new CrawlHarness();
            h.Db.CrawlRuns.Add(new CrawlRun { StartedAt = T0 });
            h.Db.SaveChanges();

            h.Db.CrawlRuns.Add(new CrawlRun { StartedAt = T0 });

            Assert.True(DatabaseErrors.IsUniqueViolation(
                Assert.Throws<DbUpdateException>(() => h.Db.SaveChanges()), "UX_CrawlRuns_OneRunning"));
        }

        [Fact]
        public void Finished_crawl_runs_do_not_count_against_the_limit()
        {
            using var h = new CrawlHarness();
            h.Db.CrawlRuns.Add(new CrawlRun { StartedAt = T0, Status = JobStatus.Completed });
            h.Db.CrawlRuns.Add(new CrawlRun { StartedAt = T0, Status = JobStatus.Failed });
            h.Db.CrawlRuns.Add(new CrawlRun { StartedAt = T0 });
            h.Db.SaveChanges();
        }
    }

    public class Cascades
    {
        [Fact]
        public void Deleting_a_product_removes_everything_that_hangs_off_it()
        {
            using var h = new CrawlHarness();
            var product = h.AddProduct();
            var keeper = h.AddProduct("N2");
            var snapshot = TestDb.AddSnapshot(h.Db, product, 100);
            TestDb.AddSnapshot(h.Db, keeper, 50);
            h.Db.DiscountFlags.Add(new DiscountFlag
            {
                ProductId = product.Id, TriggeringSnapshotId = snapshot.Id, PriorHighPrice = 150,
                PriorHighDetectedAt = T0, DiscountedPrice = 140, DiscountPercent = 30
            });
            h.Db.RestockEvents.Add(new RestockEvent { ProductId = product.Id, TriggeringSnapshotId = snapshot.Id });
            h.Db.NotificationSubscriptions.Add(new NotificationSubscription { ProductId = product.Id, TelegramChatId = 1 });
            h.Db.SaveChanges();
            h.AddCheckNow(product);
            h.AddCrawl(product);

            h.Db.Products.Remove(product);
            h.Db.SaveChanges();

            Assert.Equal(1, h.Query(db => db.Products.Count()));
            Assert.Equal(1, h.Query(db => db.PriceSnapshots.Count()));
            Assert.Equal(0, h.Query(db => db.DiscountFlags.Count()));
            Assert.Equal(0, h.Query(db => db.RestockEvents.Count()));
            Assert.Equal(0, h.Query(db => db.NotificationSubscriptions.Count()));
            Assert.Equal(0, h.Query(db => db.CheckNowRequests.Count()));
            Assert.Equal(0, h.Query(db => db.ProductCrawlRequests.Count()));
        }

        [Fact]
        public void A_snapshot_cannot_reference_a_missing_product()
        {
            using var db = TestDb.Create();
            db.PriceSnapshots.Add(new PriceSnapshot { ProductId = 12345, Price = 1, Stock = true });

            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }
    // The hot queries, and the index each one relies on. Tables are empty in a test,
    // so the planner would always pick a sequential scan; with that switched off, the
    // plan shows whether an index *can* serve the query. That's what this proves - not
    // what the planner does at production size, which I haven't measured.
    public class QueryPlans
    {
        private static string Plan(AppDbContext db, string sql)
        {
            var connection = db.Database.GetDbConnection();
            connection.Open();
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SET enable_seqscan = off; EXPLAIN " + sql;
                using var reader = command.ExecuteReader();
                var lines = new List<string>();
                while (reader.Read())
                {
                    lines.Add(reader.GetString(0));
                }

                return string.Join(" | ", lines);
            }
            finally
            {
                connection.Close();
            }
        }

        [Theory]
        [InlineData("""SELECT * FROM "ProductCrawlRequests" WHERE "ProductId" = 1 AND ("Status" = 0 OR "Status" = 3)""", "UX_ProductCrawlRequests_ActivePerProduct")]
        [InlineData("""SELECT * FROM "CheckNowRequests" WHERE "ProductId" = 1 AND ("Status" = 0 OR "Status" = 3)""", "UX_CheckNowRequests_ActivePerProduct")]
        [InlineData("""SELECT * FROM "ProductCrawlRequests" WHERE "Status" = 0 AND "RequestedAt" < now()""", "IX_ProductCrawlRequests_Status_RequestedAt")]
        [InlineData("""SELECT count(*) FROM "CheckNowRequests" WHERE "Status" = 2 AND "RequestedAt" >= now()""", "IX_CheckNowRequests_Status_RequestedAt")]
        [InlineData("""SELECT * FROM "PriceSnapshots" WHERE "ProductId" = 1 ORDER BY "CrawledAt" DESC, "Id" DESC LIMIT 1""", "IX_PriceSnapshots_ProductId_CrawledAt_Id")]
        [InlineData("""SELECT * FROM "NotificationSubscriptions" WHERE "TelegramChatId" = 1""", "IX_NotificationSubscriptions_TelegramChatId")]
        [InlineData("""SELECT * FROM "Products" WHERE "Url" = 'x'""", "IX_Products_Url")]
        public void A_hot_query_can_use_its_index(string sql, string expectedIndex)
        {
            using var db = TestDb.Create();

            var plan = Plan(db, sql);

            Assert.Contains(expectedIndex, plan);
        }
    }
}
