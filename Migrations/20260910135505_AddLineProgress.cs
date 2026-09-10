using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace HendersonSoftwareLabsAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddLineProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LineKindProgress",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HandCleared = table.Column<long>(type: "bigint", nullable: false),
                    Helpers = table.Column<int>(type: "integer", nullable: false),
                    UnlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineKindProgress", x => x.Kind);
                });

            migrationBuilder.InsertData(
                table: "LineKindProgress",
                columns: new[] { "Kind", "HandCleared", "Helpers", "UnlockedAt" },
                values: new object[,]
                {
                    { "Intake", 0L, 0, new DateTime(2026, 9, 10, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "Invoice", 0L, 0, null },
                    { "Notify", 0L, 0, null },
                    { "Report", 0L, 0, null },
                    { "Sync", 0L, 0, null },
                    { "Validate", 0L, 0, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LineKindProgress");
        }
    }
}
