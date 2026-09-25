using System.Globalization;
using System.Text;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public static class OpportunityRadarReporting
{
    public static string BuildCsv(IEnumerable<Opportunity> opportunities, RadarPreferences preferences)
    {
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "id", "entity_type", "title", "description", "declared_source_type", "source_name", "source_url", "source_date", "external_id",
                "synthetic", "duplicate_of_id", "evaluation_status", "provider", "model", "question_set_version", "recommendation",
                "priority_band", "budget_status", "website_url", "website_domain", "geography", "industry", "factors", "concerns",
                "research_confidence", "research_confidence_reason", "research_agent", "imported_prospect_type", "evaluated_prospect_type",
                "prospect_type_override", "opportunity_score", "jev_confidence", "needs_verification", "evaluation_checks",
                "rubric_version", "evaluation_origin", "effective_weights", "user_decision", "notes", "evaluated_at" }
        };
        foreach (var opportunity in opportunities)
        {
            var evaluation = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            RadarResult? result = null;
            if (evaluation is { Status: not EvaluationStatus.Failed })
            {
                try { result = JsonSerializer.Deserialize<RadarResult>(evaluation.ResultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch (JsonException) { }
            }
            var isActiveProject = opportunity.EntityType == OpportunityEntityType.ActiveProject;
            var userDecision = isActiveProject
                ? opportunity.ActiveProjectDetail?.UserDecision?.ToString()
                : opportunity.BusinessProspectDetail?.UserDecision?.ToString();
            rows.Add(new[]
            {
                opportunity.Id.ToString(CultureInfo.InvariantCulture), opportunity.EntityType.ToString(), opportunity.Title, opportunity.Description,
                isActiveProject ? opportunity.ActiveProjectDetail?.DeclaredSourceType.ToString() : null,
                opportunity.SourceName, opportunity.SourceUrl,
                opportunity.SourceDate?.ToString("O", CultureInfo.InvariantCulture), opportunity.ExternalId,
                opportunity.IsSynthetic.ToString(), opportunity.DuplicateOfId?.ToString(CultureInfo.InvariantCulture),
                evaluation?.Status.ToString(), evaluation?.Provider.ToString(), evaluation?.Model, evaluation?.QuestionSetVersion,
                evaluation?.Recommendation?.ToString(), evaluation?.PriorityBand?.ToString(), evaluation?.BudgetStatus.ToString(),
                isActiveProject ? null : opportunity.BusinessProspectDetail?.WebsiteUrl,
                isActiveProject ? null : opportunity.BusinessProspectDetail?.NormalizedWebsiteDomain,
                isActiveProject ? null : opportunity.BusinessProspectDetail?.Geography,
                isActiveProject ? null : opportunity.BusinessProspectDetail?.Industry,
                result is null ? null : string.Join(" | ", result.Factors.Select(x => $"{x.Label}: {x.Score:0.#}/100")),
                result is null ? null : string.Join(" | ", result.Concerns), opportunity.ResearchConfidence?.ToString(),
                opportunity.ResearchConfidenceReason, opportunity.ResearchAgent,
                opportunity.BusinessProspectDetail?.ImportedProspectType?.ToString(), evaluation?.EvaluatedProspectType?.ToString(),
                opportunity.BusinessProspectDetail?.ProspectTypeOverride?.ToString(), evaluation?.OpportunityScore?.ToString(CultureInfo.InvariantCulture),
                evaluation?.JevConfidence?.ToString(CultureInfo.InvariantCulture), evaluation?.NeedsVerification.ToString(),
                result?.Checks is null ? null : string.Join(" | ", result.Checks.Select(x => $"{x.Severity}:{x.Key}:{x.Explanation}")),
                evaluation?.RubricVersion, evaluation?.Origin.ToString(), evaluation?.EffectiveWeightsJson,
                userDecision, opportunity.Notes, evaluation?.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        var builder = new StringBuilder("﻿");
        foreach (var row in rows) builder.AppendLine(string.Join(',', row.Select(CsvCell)));
        return builder.ToString();
    }

    public static string SanitizeExportValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cleaned = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        var first = cleaned.AsSpan().TrimStart();
        if (!first.IsEmpty && IsFormulaPrefix(first[0])) cleaned = "'" + cleaned;
        return cleaned;
    }

    public static string CsvCell(string? value) => $"\"{SanitizeExportValue(value).Replace("\"", "\"\"")}\"";

    private static bool IsFormulaPrefix(char value) => value is '=' or '+' or '-' or '@'
        or '＝' or '＋' or '－' or '＠' or '﹦' or '﹢' or '﹣' or '﹫';
}
