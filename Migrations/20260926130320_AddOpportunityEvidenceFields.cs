using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HendersonSoftwareLabsAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddOpportunityEvidenceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntryOffer",
                table: "BusinessProspectDetails",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fit",
                table: "BusinessProspectDetails",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Risk",
                table: "BusinessProspectDetails",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Budget",
                table: "ActiveProjectDetails",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompetitionHires",
                table: "ActiveProjectDetails",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompetitionInterviewing",
                table: "ActiveProjectDetails",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompetitionProposals",
                table: "ActiveProjectDetails",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fit",
                table: "ActiveProjectDetails",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposalAngle",
                table: "ActiveProjectDetails",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Risk",
                table: "ActiveProjectDetails",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryOffer",
                table: "BusinessProspectDetails");

            migrationBuilder.DropColumn(
                name: "Fit",
                table: "BusinessProspectDetails");

            migrationBuilder.DropColumn(
                name: "Risk",
                table: "BusinessProspectDetails");

            migrationBuilder.DropColumn(
                name: "Budget",
                table: "ActiveProjectDetails");

            migrationBuilder.DropColumn(
                name: "CompetitionHires",
                table: "ActiveProjectDetails");

            migrationBuilder.DropColumn(
                name: "CompetitionInterviewing",
                table: "ActiveProjectDetails");

            migrationBuilder.DropColumn(
                name: "CompetitionProposals",
                table: "ActiveProjectDetails");

            migrationBuilder.DropColumn(
                name: "Fit",
                table: "ActiveProjectDetails");

            migrationBuilder.DropColumn(
                name: "ProposalAngle",
                table: "ActiveProjectDetails");

            migrationBuilder.DropColumn(
                name: "Risk",
                table: "ActiveProjectDetails");
        }
    }
}
