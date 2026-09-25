using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public record RadarPassage(string Id, string Text);
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

    public static IReadOnlyList<RadarPassage> Segment(string description)
    {
        var blocks = Regex.Split(description.Trim(), @"(?:\r?\n){2,}")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .SelectMany(SplitLongBlock)
            .Take(100)
            .ToList();
        return blocks.Select((text, index) => new RadarPassage($"p{index + 1}", text)).ToList();
    }

    private static IEnumerable<string> SplitLongBlock(string block)
    {
        if (block.Length <= 700) return [block];
        var parts = new List<string>();
        var offset = 0;
        while (offset < block.Length)
        {
            var length = Math.Min(700, block.Length - offset);
            if (offset + length < block.Length)
            {
                var breakAt = block.LastIndexOfAny([' ', '\r', '\n'], offset + length - 1, length);
                if (breakAt >= offset + 350) length = breakAt - offset + 1;
            }
            var passage = block.Substring(offset, length).Trim();
            if (passage.Length > 0) parts.Add(passage);
            offset += length;
        }
        return parts;
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

    // internal so OpportunityRadarV2 shares these instead of allocating its own copies.
    internal static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };
    internal static readonly JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
