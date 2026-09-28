using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public record RadarPassage(string Id, string Text, string? Source = null, DateTime? Date = null, string? Category = null);
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
    string[] PreferredProjectTypes,
    string[] ExcludedProjectTypes,
    decimal MinimumBudget,
    IncompleteInformationTolerance IncompleteInformationTolerance);

public record BusinessProspectPreferences(
    string[] PreferredIndustries,
    string[] ExcludedIndustries,
    string[] PreferredGeographies,
    string[] ExcludedGeographies);

public record HslBusinessProfile(
    string Positioning,
    string BusinessModel,
    string[] IdealCustomerTraits,
    string[] CoreOffers,
    string[] SecondaryOffers,
    string[] Capabilities,
    string[] EngagementModel,
    string[] CapacityConstraints,
    string[] GeographicFocus,
    string[] PriceBands,
    DateTime? LastReviewedAt);

public record HslBusinessProfileContext(
    string Positioning,
    string BusinessModel,
    string[] IdealCustomerTraits,
    string[] CoreOffers,
    string[] SecondaryOffers,
    string[] EngagementModel,
    string[] CapacityConstraints,
    string[] GeographicFocus,
    string[] PriceBands);

public static class OpportunityRadarEngine
{
    public static readonly HslBusinessProfile DefaultBusinessProfile = new(
        "Henderson Software Labs builds custom software, automations, and integrations that remove costly manual work for growing businesses without an internal software team.",
        "Find observable operational friction, begin with a focused paid engagement, deliver a measurable improvement, and grow into a long-term software and automation partnership.",
        [
            "Established businesses with roughly 10 to 100 employees and meaningful operational complexity.",
            "Teams relying on spreadsheets, email, PDFs, recurring reports, order processing, or disconnected systems.",
            "Businesses without an internal software team and with an identifiable owner or operational decision-maker."
        ],
        [
            "Workflow discovery and automation audits followed by a contained implementation.",
            "Workflow automation, system integrations, custom internal software, reporting, and portals.",
            "Ongoing software and automation partnership after a successful initial engagement."
        ],
        [
            "Production Readiness Audits for AI-built software.",
            "Business websites when they improve customer acquisition or connect to business operations."
        ],
        [".NET", "C#", "React", "TypeScript", "SQL", "PostgreSQL", "REST APIs", "AWS"],
        [
            "Prefer a narrow, valuable first engagement with phased delivery.",
            "Prefer client-owned software and infrastructure where practical.",
            "Use the initial project to earn trust and identify adjacent improvements."
        ],
        [
            "Delivery is centered on one experienced independent engineer, so work must support a useful contained first version.",
            "Avoid full-time employment, staff augmentation, unpaid work, and equity-only arrangements.",
            "Avoid broad enterprise transformations and core-system replacements unless a narrow integration or companion workflow is credible."
        ],
        ["Frederick and the broader Maryland region for local prospecting, with remote delivery available when the engagement is a strong fit."],
        [
            "Workflow discovery or automation audit: $750 to $1,500.",
            "Small workflow automation: $3,000 to $7,500.",
            "More substantial workflow automation: $7,500 to $15,000 or more.",
            "Custom internal software and integrations: approximately $8,000 to $30,000 or more, phased when appropriate.",
            "Production Readiness Audit: $750 to $3,500 or more depending on application size.",
            "Ongoing software partnership: approximately $250 to $3,000 or more per month depending on responsibility and improvement capacity."
        ],
        null);

    // Shared by the List and Digest/Export summary projections so the 180-char preview rule can't drift
    // between them. Only safe to call after materialization (LINQ-to-Objects) - EF Core cannot translate
    // an arbitrary method call inside a query's Select() to SQL.
    public static string Preview(string description) => description.Length > 180 ? description[..180] + "..." : description;

    // The human-override/Jev/imported precedence for a Business Prospect's type, shared by the List and
    // Digest/Export summary projections. Same materialization caveat as Preview above - List's own
    // filtering .Where() clause (which does need SQL translation) keeps this chain inlined for that reason.
    // OpportunityRadarV2.ComposeBusinessProspect intentionally does not use this: it resolves against a
    // freshly computed Jev assessment rather than a persisted EvaluatedProspectType column, and explicitly
    // treats an Unknown Jev result as "keep falling through" - a materially different rule, not the same
    // duplicated one.
    public static BusinessProspectType? ResolvedProspectType(BusinessProspectType? overrideValue, BusinessProspectType? evaluated, BusinessProspectType? imported) =>
        overrideValue ?? evaluated ?? imported;

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
            string.IsNullOrWhiteSpace(fact.Source) ? null : fact.Source.Trim(), fact.Date,
            string.IsNullOrWhiteSpace(fact.Category) ? null : fact.Category.Trim())).ToList();

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
        return Similarity(Tokens(left), right);
    }

    internal static HashSet<string> SimilarityTokens(string value) => Tokens(value);

    internal static double Similarity(HashSet<string> leftTokens, string right)
    {
        var rightTokens = Tokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0) return 0;
        return (double)leftTokens.Intersect(rightTokens).Count() / leftTokens.Union(rightTokens).Count();
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
                parsed?.PreferredProjectTypes ?? [], parsed?.ExcludedProjectTypes ?? [],
                parsed?.MinimumBudget ?? 2500m,
                Enum.TryParse<IncompleteInformationTolerance>(parsed?.IncompleteInformationTolerance, true, out var tolerance)
                    ? tolerance : IncompleteInformationTolerance.Medium);
        }
        catch (JsonException)
        {
            return new ActiveProjectPreferences([], [], 2500m, IncompleteInformationTolerance.Medium);
        }
    }

    // Read via the business profile itself - see ReadBusinessProfile. Kept as its own named helper
    // (and sent to Jev as its own hsl_capabilities field, not nested inside hsl_business_profile)
    // since both evaluators' hsl_delivery_fit wording refers to it explicitly.
    public static string[] ReadCapabilities(RadarPreferences preferences) => ReadBusinessProfile(preferences).Capabilities;

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

    public static HslBusinessProfile ReadBusinessProfile(RadarPreferences preferences)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<HslBusinessProfile>(preferences.BusinessProfileJson, CaseInsensitiveOptions);
            return parsed is not null
                && !string.IsNullOrWhiteSpace(parsed.Positioning) && !string.IsNullOrWhiteSpace(parsed.BusinessModel)
                && parsed.IdealCustomerTraits is not null && parsed.CoreOffers is not null && parsed.SecondaryOffers is not null
                && parsed.Capabilities is not null && parsed.EngagementModel is not null && parsed.CapacityConstraints is not null
                && parsed.GeographicFocus is not null && parsed.PriceBands is not null
                ? parsed : DefaultBusinessProfile;
        }
        catch (JsonException)
        {
            return DefaultBusinessProfile;
        }
    }

    // The profile always shapes both Jev prompts - there is no draft/inactive state to gate on.
    public static HslBusinessProfileContext ReadBusinessProfileContext(RadarPreferences preferences) =>
        ToContext(ReadBusinessProfile(preferences));

    public static string EffectiveQuestionSetVersion(string baseVersion, RadarPreferences preferences)
    {
        var canonical = JsonSerializer.Serialize(ReadBusinessProfileContext(preferences), CamelCaseOptions);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..8];
        return $"{baseVersion}-p{digest}";
    }

    public static string BusinessProfileDigest(RadarPreferences preferences)
    {
        var canonical = JsonSerializer.Serialize(ReadBusinessProfileContext(preferences), CamelCaseOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static HslBusinessProfileContext ToContext(HslBusinessProfile profile) => new(
        profile.Positioning, profile.BusinessModel, profile.IdealCustomerTraits, profile.CoreOffers,
        profile.SecondaryOffers, profile.EngagementModel,
        profile.CapacityConstraints, profile.GeographicFocus, profile.PriceBands);

    private static HashSet<string> Tokens(string value) => Normalize(value)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(token => token.Length > 3 && token.EndsWith('s') ? token[..^1] : token)
        .ToHashSet();

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9+#.]+", " ").Trim();

    private sealed record ActiveProjectPreferencesJson(
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
