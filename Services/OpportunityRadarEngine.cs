using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public record RadarPassage(string Id, string Text, string? Source = null, DateTime? Date = null);
public record RadarFactor(string Key, string Label, double Score, string EvidencePassageId, string Explanation);
public record EvaluationCheck(string Key, EvaluationCheckSeverity Severity, string Explanation, string EvidencePassageId = "none",
    EvaluationCheckCategory Category = EvaluationCheckCategory.Concern);

public record RadarResult(
    string? Kind,
    string ProjectType,
    OpportunityRecommendation Recommendation,
    PriorityBand PriorityBand,
    BudgetStatus BudgetStatus,
    IReadOnlyList<RadarFactor> Factors,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> MissingInformation,
    IReadOnlyList<string> Concerns,
    string Summary,
    string NextStep,
    decimal? OpportunityScore = null,
    decimal? JevConfidence = null,
    BusinessProspectType? ProspectType = null,
    bool NeedsVerification = false,
    IReadOnlyList<EvaluationCheck>? Checks = null,
    IReadOnlyDictionary<string, decimal>? EffectiveWeights = null,
    string RubricVersion = "radar-v1");

public record ActiveProjectPreferences(
    string[] Capabilities,
    string[] PreferredProjectTypes,
    string[] ExcludedProjectTypes,
    decimal MinimumBudget,
    IncompleteInformationTolerance IncompleteInformationTolerance);

public record BusinessProspectPreferences(
    string[] PreferredIndustries,
    string[] ExcludedIndustries,
    string[] PreferredGeographies,
    string[] ExcludedGeographies);

public static class OpportunityRadarEngine
{
    public static string SerializePassages(IReadOnlyList<RadarPassage> passages) =>
        JsonSerializer.Serialize(passages, CamelCaseOptions);

    public static List<RadarPassage> DeserializePassages(string json) =>
        JsonSerializer.Deserialize<List<RadarPassage>>(json, CaseInsensitiveOptions) ?? [];

    // Passages come directly from the structured import contract now (one per fixed Active Project
    // field, one per Business Prospect evidence fact) instead of being mechanically guessed from a
    // flat text blob. See ImportService.BuildActiveProjectPassages/BuildBusinessProspectPassages.
    public static List<RadarPassage> BuildActiveProjectPassages(string request, string? budget, CompetitionInfo? competition)
    {
        var passages = new List<RadarPassage> { new("request", request.Trim()) };
        if (!string.IsNullOrWhiteSpace(budget)) passages.Add(new RadarPassage("budget", budget.Trim()));
        if (competition is not null) passages.Add(new RadarPassage("competition", RenderCompetition(competition)));
        return passages;
    }

    public static List<RadarPassage> BuildBusinessProspectPassages(IReadOnlyList<EvidenceFact> facts) =>
        facts.Select((fact, index) => new RadarPassage($"fact-{index + 1}", fact.Fact.Trim(),
            string.IsNullOrWhiteSpace(fact.Source) ? null : fact.Source.Trim(), fact.Date)).ToList();

    public static string ComposeActiveProjectDescription(ActiveProjectImportRequest request) => string.Join("\n\n",
        new[]
        {
            $"Request:\n{request.Request.Trim()}",
            string.IsNullOrWhiteSpace(request.Budget) ? null : $"Budget:\n{request.Budget.Trim()}",
            request.Competition is null ? null : $"Competition:\n{RenderCompetition(request.Competition)}",
            string.IsNullOrWhiteSpace(request.Fit) ? null : $"Fit:\n{request.Fit.Trim()}",
            string.IsNullOrWhiteSpace(request.ProposalAngle) ? null : $"Proposal angle:\n{request.ProposalAngle.Trim()}",
            string.IsNullOrWhiteSpace(request.Risk) ? null : $"Risk:\n{request.Risk.Trim()}"
        }.Where(x => x is not null));

    public static string ComposeBusinessProspectDescription(BusinessProspectImportRequest request) => string.Join("\n\n",
        new[]
        {
            string.IsNullOrWhiteSpace(request.Fit) ? null : $"Fit:\n{request.Fit.Trim()}",
            string.IsNullOrWhiteSpace(request.EntryOffer) ? null : $"Entry offer:\n{request.EntryOffer.Trim()}",
            string.IsNullOrWhiteSpace(request.Risk) ? null : $"Risk:\n{request.Risk.Trim()}",
            "Evidence:\n" + string.Join("\n", request.Evidence.Select(fact => "- " + fact.Fact.Trim()
                + (string.IsNullOrWhiteSpace(fact.Source) ? "" : $" (Source: {fact.Source.Trim()})")
                + (fact.Date is { } date ? $" ({date:yyyy-MM-dd})" : "")))
        }.Where(x => x is not null));

    private static string RenderCompetition(CompetitionInfo competition)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(competition.Proposals)) parts.Add($"Proposals: {competition.Proposals.Trim()}");
        if (competition.Interviewing is { } interviewing) parts.Add($"Interviewing: {interviewing}");
        if (competition.Hires is { } hires) parts.Add($"Hires: {hires}");
        return string.Join(", ", parts);
    }

    public static string Fingerprint(string title, string description, string? sourceUrl)
    {
        var normalized = Normalize($"{title} {description} {sourceUrl}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static double Similarity(string left, string right)
    {
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return 0;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }

    public static string NormalizeBusinessName(string name) => Normalize(name);

    public static string NormalizeWebsiteDomain(string? websiteUrl)
    {
        if (string.IsNullOrWhiteSpace(websiteUrl) || !Uri.TryCreate(websiteUrl, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    public static string Serialize(RadarResult result) => JsonSerializer.Serialize(result, CamelCaseOptions);

    public static ActiveProjectPreferences ReadActiveProjectPreferences(RadarPreferences preferences)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ActiveProjectPreferencesJson>(preferences.ActiveProjectPreferencesJson, CaseInsensitiveOptions);
            return new ActiveProjectPreferences(
                parsed?.Capabilities ?? [], parsed?.PreferredProjectTypes ?? [], parsed?.ExcludedProjectTypes ?? [],
                parsed?.MinimumBudget ?? 2500m,
                Enum.TryParse<IncompleteInformationTolerance>(parsed?.IncompleteInformationTolerance, true, out var tolerance)
                    ? tolerance : IncompleteInformationTolerance.Medium);
        }
        catch (JsonException)
        {
            return new ActiveProjectPreferences([], [], [], 2500m, IncompleteInformationTolerance.Medium);
        }
    }

    public static BusinessProspectPreferences ReadBusinessProspectPreferences(RadarPreferences preferences)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BusinessProspectPreferencesJson>(preferences.BusinessProspectPreferencesJson, CaseInsensitiveOptions);
            return new BusinessProspectPreferences(
                parsed?.PreferredIndustries ?? [], parsed?.ExcludedIndustries ?? [],
                parsed?.PreferredGeographies ?? [], parsed?.ExcludedGeographies ?? []);
        }
        catch (JsonException)
        {
            return new BusinessProspectPreferences([], [], [], []);
        }
    }

    private static HashSet<string> Tokens(string value) => Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(token => token.Length > 3 && token.EndsWith('s') ? token[..^1] : token)
        .ToHashSet();

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9+#.]+", " ").Trim();

    private sealed record ActiveProjectPreferencesJson(
        string[]? Capabilities,
        string[]? PreferredProjectTypes,
        string[]? ExcludedProjectTypes,
        decimal? MinimumBudget,
        string? IncompleteInformationTolerance);

    private sealed record BusinessProspectPreferencesJson(
        string[]? PreferredIndustries,
        string[]? ExcludedIndustries,
        string[]? PreferredGeographies,
        string[]? ExcludedGeographies);

    // Both need a string-enum converter: without one, System.Text.Json writes enums (EvaluationCheckSeverity,
    // EvaluationCheckCategory, BusinessProspectType, etc.) embedded in these JSON blobs as their raw numeric
    // value, silently breaking the frontend's severity === 'Review' style comparisons (they'd always see a
    // number, never the string TypeScript's contract promises). JsonStringEnumConverter's reader still accepts
    // a bare number too, so this stays backward compatible with rows written before this fix - see
    // EvaluationCheckSeverityJsonRoundTrip in tests/OpportunityRadar.Unit for both directions.
    // internal so OpportunityRadarV2 shares these instead of allocating its own copies.
    internal static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    internal static readonly JsonSerializerOptions CamelCaseOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
}
