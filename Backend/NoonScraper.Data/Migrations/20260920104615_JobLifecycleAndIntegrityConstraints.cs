using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NoonScraper.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobLifecycleAndIntegrityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RestockEvents_TriggeringSnapshotId",
                table: "RestockEvents");

            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_ProductId_CrawledAt",
                table: "PriceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_DiscountFlags_TriggeringSnapshotId",
                table: "DiscountFlags");

            migrationBuilder.DropIndex(
                name: "IX_CheckNowRequests_ProductId",
                table: "CheckNowRequests");

            migrationBuilder.AddColumn<decimal>(
                name: "HistoricalLowPrice",
                table: "DiscountFlags",
                type: "numeric",
                nullable: true);

            // The column is narrowed to 1000 characters below; rows written before now
            // could hold a whole exception message (Playwright's include call logs).
            migrationBuilder.Sql(
                "UPDATE \"CheckNowRequests\" SET \"ErrorMessage\" = LEFT(\"ErrorMessage\", 1000) WHERE length(\"ErrorMessage\") > 1000;");

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "CheckNowRequests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "CheckNowRequests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "FailureStage",
                table: "CheckNowRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GitHubRunId",
                table: "CheckNowRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "CheckNowRequests",
                type: "timestamp with time zone",
                nullable: true);

            // ---- Existing data, made valid for the constraints created below. ----

            // Requests that existed before the job lifecycle were never given a
            // correlation id; the column default is all zeros, so give each its own.
            migrationBuilder.Sql("UPDATE \"CheckNowRequests\" SET \"CorrelationId\" = gen_random_uuid();");

            // At most one active (Pending/Running) check per product is now enforced.
            // A row still Pending at migration time can't have a worker coming for it
            // (nothing ever claimed requests before this migration), so it is closed
            // as failed rather than left to block the product.
            migrationBuilder.Sql(
                "UPDATE \"CheckNowRequests\" SET \"Status\" = 2, \"CompletedAt\" = NOW(), \"FailureStage\" = 'migration', " +
                "\"ErrorMessage\" = COALESCE(\"ErrorMessage\", 'Closed by a migration: this request was still pending.') " +
                "WHERE \"Status\" = 0;");

            // One flag / one restock per triggering snapshot is now enforced. The code
            // never wrote more than one, so this should remove nothing; if a
            // duplicate exists it is redundant by definition, and the oldest is kept.
            migrationBuilder.Sql(
                "DELETE FROM \"DiscountFlags\" d USING \"DiscountFlags\" keep " +
                "WHERE d.\"TriggeringSnapshotId\" = keep.\"TriggeringSnapshotId\" AND d.\"Id\" > keep.\"Id\";");
            migrationBuilder.Sql(
                "DELETE FROM \"RestockEvents\" r USING \"RestockEvents\" keep " +
                "WHERE r.\"TriggeringSnapshotId\" = keep.\"TriggeringSnapshotId\" AND r.\"Id\" > keep.\"Id\";");

            migrationBuilder.CreateTable(
                name: "CrawlRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GitHubRunId = table.Column<long>(type: "bigint", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoriesAttempted = table.Column<int>(type: "integer", nullable: false),
                    CategoriesFailed = table.Column<int>(type: "integer", nullable: false),
                    ProductsAttempted = table.Column<int>(type: "integer", nullable: false),
                    ProductsSucceeded = table.Column<int>(type: "integer", nullable: false),
                    ProductsFailed = table.Column<int>(type: "integer", nullable: false),
                    ProductsDeferred = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrawlRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductCrawlRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FailureStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubRunId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCrawlRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCrawlRequests_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestockEvents_TriggeringSnapshotId",
                table: "RestockEvents",
                column: "TriggeringSnapshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_ProductId_CrawledAt_Id",
                table: "PriceSnapshots",
                columns: new[] { "ProductId", "CrawledAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscriptions_TelegramChatId",
                table: "NotificationSubscriptions",
                column: "TelegramChatId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountFlags_TriggeringSnapshotId",
                table: "DiscountFlags",
                column: "TriggeringSnapshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckNowRequests_Status_RequestedAt",
                table: "CheckNowRequests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_CheckNowRequests_ActivePerProduct",
                table: "CheckNowRequests",
                column: "ProductId",
                unique: true,
                filter: "\"Status\" IN (0, 3)");

            migrationBuilder.CreateIndex(
                name: "UX_CrawlRuns_OneRunning",
                table: "CrawlRuns",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 3");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCrawlRequests_Status_RequestedAt",
                table: "ProductCrawlRequests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_ProductCrawlRequests_ActivePerProduct",
                table: "ProductCrawlRequests",
                column: "ProductId",
                unique: true,
                filter: "\"Status\" IN (0, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrawlRuns");

            migrationBuilder.DropTable(
                name: "ProductCrawlRequests");

            migrationBuilder.DropIndex(
                name: "IX_RestockEvents_TriggeringSnapshotId",
                table: "RestockEvents");

            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_ProductId_CrawledAt_Id",
                table: "PriceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_NotificationSubscriptions_TelegramChatId",
                table: "NotificationSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_DiscountFlags_TriggeringSnapshotId",
                table: "DiscountFlags");

            migrationBuilder.DropIndex(
                name: "IX_CheckNowRequests_Status_RequestedAt",
                table: "CheckNowRequests");

            migrationBuilder.DropIndex(
                name: "UX_CheckNowRequests_ActivePerProduct",
                table: "CheckNowRequests");

            migrationBuilder.DropColumn(
                name: "HistoricalLowPrice",
                table: "DiscountFlags");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "CheckNowRequests");

            migrationBuilder.DropColumn(
                name: "FailureStage",
                table: "CheckNowRequests");

            migrationBuilder.DropColumn(
                name: "GitHubRunId",
                table: "CheckNowRequests");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "CheckNowRequests");

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "CheckNowRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RestockEvents_TriggeringSnapshotId",
                table: "RestockEvents",
                column: "TriggeringSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_ProductId_CrawledAt",
                table: "PriceSnapshots",
                columns: new[] { "ProductId", "CrawledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscountFlags_TriggeringSnapshotId",
                table: "DiscountFlags",
                column: "TriggeringSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckNowRequests_ProductId",
                table: "CheckNowRequests",
                column: "ProductId");
        }
    }
}
