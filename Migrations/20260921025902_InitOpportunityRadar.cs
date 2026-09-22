using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HendersonSoftwareLabsAPI.Migrations
{
    /// <inheritdoc />
    public partial class InitOpportunityRadar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Opportunities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: false),
                    SourceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourcePassagesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DuplicateOfId = table.Column<int>(type: "integer", nullable: true),
                    IsSynthetic = table.Column<bool>(type: "boolean", nullable: false),
                    SyntheticKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Opportunities_Opportunities_DuplicateOfId",
                        column: x => x.DuplicateOfId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RadarPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ActiveProjectPreferencesJson = table.Column<string>(type: "jsonb", nullable: false),
                    BusinessProspectPreferencesJson = table.Column<string>(type: "jsonb", nullable: false),
                    DigestActiveProjectCount = table.Column<int>(type: "integer", nullable: false),
                    DigestBusinessProspectCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RadarPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RadarPreferences_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActiveProjectDetails",
                columns: table => new
                {
                    OpportunityId = table.Column<int>(type: "integer", nullable: false),
                    DeclaredSourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserDecision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveProjectDetails", x => x.OpportunityId);
                    table.ForeignKey(
                        name: "FK_ActiveProjectDetails_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessProspectDetails",
                columns: table => new
                {
                    OpportunityId = table.Column<int>(type: "integer", nullable: false),
                    NormalizedBusinessName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WebsiteUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NormalizedWebsiteDomain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Geography = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Industry = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UserDecision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessProspectDetails", x => x.OpportunityId);
                    table.ForeignKey(
                        name: "FK_BusinessProspectDetails_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpportunityEvaluations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OpportunityId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    QuestionSetVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PriorityBand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BudgetStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssessmentJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProviderResponseJson = table.Column<string>(type: "jsonb", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    NextStep = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpportunityEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpportunityEvaluations_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessProspectDetails_NormalizedBusinessName",
                table: "BusinessProspectDetails",
                column: "NormalizedBusinessName");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessProspectDetails_NormalizedWebsiteDomain",
                table: "BusinessProspectDetails",
                column: "NormalizedWebsiteDomain");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_DuplicateOfId",
                table: "Opportunities",
                column: "DuplicateOfId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_EntityType",
                table: "Opportunities",
                column: "EntityType");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_Fingerprint",
                table: "Opportunities",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_SyntheticKey",
                table: "Opportunities",
                column: "SyntheticKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpportunityEvaluations_OpportunityId_CreatedAt",
                table: "OpportunityEvaluations",
                columns: new[] { "OpportunityId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RadarPreferences_OwnerUserId",
                table: "RadarPreferences",
                column: "OwnerUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveProjectDetails");

            migrationBuilder.DropTable(
                name: "BusinessProspectDetails");

            migrationBuilder.DropTable(
                name: "OpportunityEvaluations");

            migrationBuilder.DropTable(
                name: "RadarPreferences");

            migrationBuilder.DropTable(
                name: "Opportunities");
        }
    }
}
