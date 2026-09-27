using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HendersonSoftwareLabsAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddRadarBusinessProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessProfileJson",
                table: "RadarPreferences",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BusinessProfileJson",
                table: "RadarPreferences");
        }
    }
}
