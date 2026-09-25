using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HendersonSoftwareLabsAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddOpportunityRadarJevV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectiveWeightsJson",
                table: "OpportunityEvaluations",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "EvaluatedProspectType",
                table: "OpportunityEvaluations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "JevConfidence",
                table: "OpportunityEvaluations",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsVerification",
                table: "OpportunityEvaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "OpportunityScore",
                table: "OpportunityEvaluations",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "OpportunityEvaluations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "ProviderRun");

            migrationBuilder.AddColumn<string>(
                name: "RubricVersion",
                table: "OpportunityEvaluations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "radar-v1");

            migrationBuilder.AddColumn<int>(
                name: "SourceEvaluationId",
                table: "OpportunityEvaluations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResearchAgent",
                table: "Opportunities",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResearchConfidence",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResearchConfidenceReason",
                table: "Opportunities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedProspectType",
                table: "BusinessProspectDetails",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProspectTypeOverride",
                table: "BusinessProspectDetails",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("UPDATE \"OpportunityEvaluations\" SET \"Status\" = 'Stale', \"NeedsVerification\" = TRUE WHERE \"Status\" = 'Ready';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveWeightsJson",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "EvaluatedProspectType",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "JevConfidence",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "NeedsVerification",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "OpportunityScore",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "RubricVersion",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "SourceEvaluationId",
                table: "OpportunityEvaluations");

            migrationBuilder.DropColumn(
                name: "ResearchAgent",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ResearchConfidence",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ResearchConfidenceReason",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ImportedProspectType",
                table: "BusinessProspectDetails");

            migrationBuilder.DropColumn(
                name: "ProspectTypeOverride",
                table: "BusinessProspectDetails");
        }
    }
}
