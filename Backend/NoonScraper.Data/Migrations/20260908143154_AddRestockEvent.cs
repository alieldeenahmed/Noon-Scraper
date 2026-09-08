using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NoonScraper.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRestockEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RestockEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    TriggeringSnapshotId = table.Column<int>(type: "integer", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestockEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestockEvents_PriceSnapshots_TriggeringSnapshotId",
                        column: x => x.TriggeringSnapshotId,
                        principalTable: "PriceSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RestockEvents_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestockEvents_ProductId",
                table: "RestockEvents",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_RestockEvents_TriggeringSnapshotId",
                table: "RestockEvents",
                column: "TriggeringSnapshotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestockEvents");
        }
    }
}
