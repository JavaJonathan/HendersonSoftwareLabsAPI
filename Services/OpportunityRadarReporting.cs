using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public record KeywordRule(string Key, bool Triggered, string Explanation);
public record KeywordBaselineResult(
    IReadOnlyList<string> MatchedTerms,
    int KeywordScore,
    OpportunityRecommendation Recommendation,
    BudgetStatus BudgetStatus,
    IReadOnlyList<KeywordRule> HardRules,
    string Summary);

public static partial class OpportunityRadarReporting
{
    private static readonly (string Needle, string Label)[] BaselineTerms =
    [
        (".net", ".NET"), ("c#", "C#"), ("react", "React"), ("sql", "SQL"), ("api", "API"),
        ("aws", "AWS"), ("integration", "Integration"), ("automation", "Automation"),
        ("workflow", "Workflow"), ("report", "Reporting"), ("portal", "Portal"),
        ("shopify", "Shopify"), ("spreadsheet", "Spreadsheet")
    ];

    // Keyword baseline is Active Project-only: there is no natural literal-keyword equivalent to
    // "is this business's web presence weak" the way there is for demand text. Callers must guard
    // opportunity.EntityType before calling this.
    public static KeywordBaselineResult KeywordBaseline(Opportunity opportunity, RadarPreferences preferences)
    {
        var text = $"{opportunity.Title} {opportunity.Description}".ToLowerInvariant();
        var matches = BaselineTerms.Where(x => text.Contains(x.Needle, StringComparison.Ordinal))
            .Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var fullTime = ContainsAny(text, "full-time", "full time", "salary", "employee benefits", "40 hours per week");
        var teamScale = ContainsAny(text, "multi-year", "team of", "multiple developers", "entire team", "staff augmentation");
        var prefs = OpportunityRadarEngine.ReadActiveProjectPreferences(preferences);
        var budget = ParseBudget(text, prefs.MinimumBudget);
        var notExplicitDemand = (opportunity.ActiveProjectDetail?.DeclaredSourceType ?? ActiveProjectSourceType.ExplicitDemand) != ActiveProjectSourceType.ExplicitDemand;
        var rules = new List<KeywordRule>
        {
            new("full_time_role", fullTime, fullTime ? "Employment language is a hard exclusion." : "No full-time employment language detected."),
            new("team_scale", teamScale, teamScale ? "Team-scale or multi-year language is a hard exclusion." : "No team-scale hard exclusion detected."),
            new("incompatible_budget", budget == BudgetStatus.Incompatible, budget == BudgetStatus.Incompatible
                ? $"The stated budget is below the configured {prefs.MinimumBudget.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} floor."
                : budget == BudgetStatus.Unknown ? "Budget is unknown, not inadequate." : "The stated budget clears the configured floor."),
            new("not_explicit_demand", notExplicitDemand, notExplicitDemand
                ? "The declared source is a signal, not an explicit request for software help." : "The declared source is explicit demand.")
        };
        var keywordScore = matches.Count >= 4 ? 3 : matches.Count >= 2 ? 2 : matches.Count == 1 ? 1 : 0;
        var recommendation = rules.Any(x => x.Triggered) ? OpportunityRecommendation.Pass
            : keywordScore >= 3 ? OpportunityRecommendation.Pursue
            : keywordScore >= 1 ? OpportunityRecommendation.Investigate : OpportunityRecommendation.Pass;
        var summary = rules.Any(x => x.Triggered)
            ? "Keyword matches were overridden by an explicit hard rule."
            : keywordScore == 0 ? "No configured baseline terms matched."
            : $"Matched {matches.Count} literal term{(matches.Count == 1 ? "" : "s")} without contextual interpretation.";
        return new KeywordBaselineResult(matches, keywordScore, recommendation, budget, rules, summary);
    }

    public static string BuildCsv(IEnumerable<Opportunity> opportunities, RadarPreferences preferences)
    {
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "id", "entity_type", "title", "description", "declared_source_type", "source_name", "source_url", "source_date", "external_id",
                "synthetic", "duplicate_of_id", "evaluation_status", "provider", "model", "question_set_version", "recommendation",
                "priority_band", "budget_status", "website_url", "website_domain", "geography", "industry", "factors", "concerns",
                "keyword_baseline_recommendation", "keyword_baseline_score", "keyword_matches", "keyword_hard_rules", "user_decision", "notes", "evaluated_at" }
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
            var baseline = isActiveProject ? KeywordBaseline(opportunity, preferences) : null;
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
                result is null ? null : string.Join(" | ", result.Factors.Select(x => $"{x.Label}: {x.Score}/3")),
                result is null ? null : string.Join(" | ", result.Concerns), baseline?.Recommendation.ToString(),
                baseline?.KeywordScore.ToString(CultureInfo.InvariantCulture), baseline is null ? null : string.Join(" | ", baseline.MatchedTerms),
                baseline is null ? null : string.Join(" | ", baseline.HardRules.Where(x => x.Triggered).Select(x => x.Key)),
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
    private static bool ContainsAny(string text, params string[] values) => values.Any(text.Contains);
    private static BudgetStatus ParseBudget(string text, decimal floor)
    {
        var match = BudgetRegex().Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var value)) return BudgetStatus.Unknown;
        if (match.Groups[2].Value.Equals("k", StringComparison.OrdinalIgnoreCase)) value *= 1000;
        return value < floor ? BudgetStatus.Incompatible : BudgetStatus.Compatible;
    }

    [GeneratedRegex(@"\$\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*([kK]?)", RegexOptions.CultureInvariant)]
    private static partial Regex BudgetRegex();
}
