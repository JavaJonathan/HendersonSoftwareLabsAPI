using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HendersonSoftwareLabsAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddOpportunityRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpportunityRating",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpportunityRating",
                table: "Opportunities");
        }
    }
}
