using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HendersonSoftwareLabsAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddOpportunityExternalIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_EntityType_ExternalId",
                table: "Opportunities",
                columns: new[] { "EntityType", "ExternalId" },
                filter: "\"ExternalId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Opportunities_EntityType_ExternalId",
                table: "Opportunities");
        }
    }
}
