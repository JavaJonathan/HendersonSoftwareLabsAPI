using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public record RadarPassage(string Id, string Text);
public record RadarFactor(string Key, string Label, int Score, string EvidencePassageId, string Explanation);

public record ActiveProjectAssessment(
    ActiveProjectKind Kind,
    string ProjectType,
    int ProblemScore,
    int CapabilityScore,
    int ScopeScore,
    int InformationScore,
    string DependencyRisk,
    string ProblemEvidencePassageId,
    string FitEvidencePassageId,
    string ConcernEvidencePassageId);

public record BusinessProspectAssessment(
    string BuyingIntent,
    int BusinessStrengthScore,
    int DigitalPresenceWeaknessScore,
    int ReputationWebsiteMismatchScore,
    int EntryProjectStrengthScore,
    int ContactabilityScore,
    int EvidenceCompletenessScore,
    string EntryProjectEvidencePassageId,
    string ReputationEvidencePassageId,
    string ContactEvidencePassageId);

public record RadarResult(
    string? Kind,
    string ProjectType,
    OpportunityRecommendation Recommendation,
    PriorityBand PriorityBand,
    BudgetStatus BudgetStatus,
    IReadOnlyList<RadarFactor> Factors,
    IReadOnlyList<string> KnownFacts,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> MissingInformation,
    IReadOnlyList<string> Concerns,
    IReadOnlyList<string> KeywordMatches,
    string Summary,
    string NextStep);

public readonly record struct ActiveProjectWeights(int CapabilityFit, int ProblemClarity, int IndependentScope, int InformationSufficiency);
public readonly record struct BusinessProspectWeights(int BusinessStrength, int DigitalPresenceWeakness, int ReputationMismatch,
    int EntryProjectStrength, int Geography, int Contactability, int EvidenceCompleteness);

public record ActiveProjectPreferences(string[] Capabilities, string[] PreferredProjectTypes, string[] ExcludedProjectTypes,
    decimal MinimumBudget, int MinimumWeeks, int MaximumWeeks, IncompleteInformationTolerance IncompleteInformationTolerance,
    ActiveProjectWeights Weights);

public record BusinessProspectPreferences(string[] PreferredIndustries, string[] ExcludedIndustries,
    string[] PreferredGeographies, string[] ExcludedGeographies, BusinessProspectWeights Weights);

public static partial class OpportunityRadarEngine
{
    private static readonly string[] TechnologyKeywords = [".net", "c#", "react", "sql", "api", "integration", "automation", "workflow", "reporting", "portal"];

    // The passage JSON round-trips through the frontend's camelCase RadarPassage TS interface
    // ({id, text}), so it must always be serialized with the same CamelCaseOptions() used
    // elsewhere - plain JsonSerializer.Serialize(passages) would emit PascalCase (Id/Text) and
    // leave every passage id/text undefined in the UI.
    public static string SerializePassages(IReadOnlyList<RadarPassage> passages) => JsonSerializer.Serialize(passages, CamelCaseOptions());
    public static List<RadarPassage> DeserializePassages(string json) => JsonSerializer.Deserialize<List<RadarPassage>>(json, JsonOptions()) ?? [];

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
        var intersection = a.Intersect(b).Count();
        return (double)intersection / a.Union(b).Count();
    }

    public static string NormalizeBusinessName(string name) => Normalize(name);

    public static string NormalizeWebsiteDomain(string? websiteUrl)
    {
        if (string.IsNullOrWhiteSpace(websiteUrl) || !Uri.TryCreate(websiteUrl, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static HashSet<string> Tokens(string value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(token => token.Length > 3 && token.EndsWith('s') ? token[..^1] : token).ToHashSet();
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9+#.]+", " ").Trim();

    // --- Active project ---------------------------------------------------------------------

    public static (RadarResult Result, ActiveProjectAssessment Assessment) EvaluateActiveProject(Opportunity opportunity, RadarPreferences preferences)
    {
        var text = $"{opportunity.Title} {opportunity.Description}".ToLowerInvariant();
        var passages = OpportunityRadarEngine.DeserializePassages(opportunity.SourcePassagesJson);
        var evidence = passages.FirstOrDefault()?.Id ?? "none";
        var declaredSourceType = opportunity.ActiveProjectDetail?.DeclaredSourceType ?? ActiveProjectSourceType.ExplicitDemand;
        var fullTime = ContainsAny(text, "full-time", "full time", "salary", "employee benefits", "40 hours per week");
        var kind = fullTime ? ActiveProjectKind.FullTimeRole : declaredSourceType switch
        {
            ActiveProjectSourceType.OperationalSignal => ActiveProjectKind.OperationalSignal,
            _ => ActiveProjectKind.ExplicitDemand
        };
        var projectType = ClassifyProjectType(text);
        var prefs = ReadActiveProjectPreferences(preferences);
        var capabilityHits = prefs.Capabilities.Where(x => text.Contains(x.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
        var semanticFit = ContainsAny(text, "integrat", "automat", "workflow", "internal tool", "report", "portal", "reconcil", "spreadsheet", "rekey", "repeated entry") ? 3 : capabilityHits.Count > 0 ? 2 : 1;
        var concrete = ContainsAny(text, "need", "build", "create", "connect", "replace", "automate", "reconcile") ? 3 : text.Length > 250 ? 2 : 1;
        var scope = fullTime || ContainsAny(text, "enterprise platform", "entire team", "multiple developers", "multi-year") ? 0
            : ContainsAny(text, "ongoing", "long-term") ? 1 : 3;
        var budget = ParseBudgetStatus(text, prefs.MinimumBudget, out _);
        var information = text.Length >= 500 ? 3 : text.Length >= 180 || budget != BudgetStatus.Unknown && DurationRegex().IsMatch(text) ? 2 : 1;
        var dependencyRisk = ContainsAny(text, "api access", "undocumented api", "vendor approval", "pending access");
        var assessment = new ActiveProjectAssessment(kind, projectType, concrete, semanticFit, scope, information,
            dependencyRisk ? "ThirdPartyApi" : "None", evidence, evidence, evidence);
        var result = ComposeActiveProject(opportunity, preferences, assessment,
            ["Opportunity kind and fit are simulated judgments for demonstration purposes."]);
        return (result, assessment);
    }

    public static RadarResult ComposeActiveProject(Opportunity opportunity, RadarPreferences preferences, ActiveProjectAssessment assessment,
        IReadOnlyList<string> hypotheses)
    {
        var text = $"{opportunity.Title} {opportunity.Description}".ToLowerInvariant();
        var prefs = ReadActiveProjectPreferences(preferences);
        var preferredProject = prefs.PreferredProjectTypes.Contains(assessment.ProjectType, StringComparer.OrdinalIgnoreCase);
        var excludedProject = prefs.ExcludedProjectTypes.Contains(assessment.ProjectType, StringComparer.OrdinalIgnoreCase);
        var capabilityHits = prefs.Capabilities.Where(x => text.Contains(x.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
        var durationWeeks = ParseDurationWeeks(text);
        var outsidePreferredDuration = durationWeeks is not null && (durationWeeks < prefs.MinimumWeeks || durationWeeks > prefs.MaximumWeeks);
        var budget = ParseBudgetStatus(text, prefs.MinimumBudget, out var statedBudget);
        var fullTime = assessment.Kind == ActiveProjectKind.FullTimeRole;
        var dependencyRisk = !assessment.DependencyRisk.Equals("None", StringComparison.OrdinalIgnoreCase);
        var information = Math.Clamp(assessment.InformationScore, 0, 3);
        var scope = Math.Clamp(assessment.ScopeScore, 0, 3);
        var concrete = Math.Clamp(assessment.ProblemScore, 0, 3);
        var semanticFit = Math.Clamp(assessment.CapabilityScore, 0, 3);
        var hardPass = fullTime || budget == BudgetStatus.Incompatible || scope == 0 || excludedProject;
        var adjustedInformation = prefs.IncompleteInformationTolerance switch
        {
            IncompleteInformationTolerance.Low => Math.Max(0, information - 1),
            IncompleteInformationTolerance.High => Math.Min(3, information + 1),
            _ => information
        };
        var weights = prefs.Weights;
        var composite = semanticFit * weights.CapabilityFit + concrete * weights.ProblemClarity
            + scope * weights.IndependentScope + adjustedInformation * weights.InformationSufficiency + (preferredProject ? 15 : 0);
        var priority = composite >= 250 ? PriorityBand.High : composite >= 170 ? PriorityBand.Medium : PriorityBand.Low;
        var recommendation = hardPass || priority == PriorityBand.Low ? OpportunityRecommendation.Pass
            : assessment.Kind != ActiveProjectKind.ExplicitDemand || budget == BudgetStatus.Unknown || dependencyRisk || information < 2 || scope < 2 || outsidePreferredDuration
                ? OpportunityRecommendation.Investigate : OpportunityRecommendation.Pursue;
        var keywordMatches = TechnologyKeywords.Where(text.Contains).ToList();
        var known = new List<string> { $"Declared source type: {(opportunity.ActiveProjectDetail?.DeclaredSourceType.ToString() ?? assessment.Kind.ToString())}." };
        if (statedBudget is not null) known.Add($"A stated budget of {statedBudget.Value.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} was detected.");
        if (durationWeeks is not null) known.Add($"A delivery duration of approximately {durationWeeks:0.#} weeks was detected.");
        if (capabilityHits.Count > 0) known.Add($"The source explicitly mentions: {string.Join(", ", capabilityHits)}.");
        var missing = new List<string>();
        if (budget == BudgetStatus.Unknown) missing.Add("Budget is not stated.");
        if (!DurationRegex().IsMatch(text)) missing.Add("Delivery timeline is not stated.");
        if (information < 2) missing.Add("The request needs clearer outcomes and constraints.");
        var concerns = new List<string>();
        if (fullTime) concerns.Add("This appears to be a full-time employment role, not project demand.");
        if (budget == BudgetStatus.Incompatible) concerns.Add($"The stated budget is below the configured {prefs.MinimumBudget:C0} floor.");
        if (dependencyRisk) concerns.Add("Delivery may depend on API access that has not been confirmed.");
        if (durationWeeks > prefs.MaximumWeeks) concerns.Add($"The stated duration exceeds the configured {prefs.MaximumWeeks}-week preference.");
        if (durationWeeks is not null && durationWeeks < prefs.MinimumWeeks) concerns.Add($"The stated duration is shorter than the configured {prefs.MinimumWeeks}-week preference.");
        if (assessment.Kind != ActiveProjectKind.ExplicitDemand) concerns.Add("This is a signal, not evidence of intent to buy software.");
        if (excludedProject) concerns.Add($"The project type {assessment.ProjectType} is excluded by the current preferences.");
        var factors = new List<RadarFactor>
        {
            new("problem", "Problem clarity", concrete, assessment.ProblemEvidencePassageId, concrete >= 2 ? "A recognizable software problem is described." : "The desired outcome is vague."),
            new("fit", "HSL capability fit", semanticFit, assessment.FitEvidencePassageId, semanticFit >= 3 ? "The work aligns with HSL integration and automation strengths." : "The fit is adjacent or unclear."),
            new("scope", "Independent scope", scope, assessment.ConcernEvidencePassageId, scope >= 2 ? "The work appears plausible for an independent engineer." : "The scope appears ongoing or team-scale."),
            new("information", "Information sufficiency", adjustedInformation, assessment.ProblemEvidencePassageId, information >= 2 ? "There is enough detail for an initial review." : "More detail is required before assessment.")
        };
        var summary = recommendation switch
        {
            OpportunityRecommendation.Pursue => "Strong explicit demand with a concrete problem and good HSL fit.",
            OpportunityRecommendation.Investigate => "Potentially relevant, but an important unknown should be resolved first.",
            _ => "The current evidence does not justify spending prospecting time here."
        };
        var nextStep = concerns.FirstOrDefault() ?? missing.FirstOrDefault() ?? "Confirm decision-maker, timing, and procurement process.";
        return new RadarResult(assessment.Kind.ToString(), assessment.ProjectType, recommendation, priority, budget, factors, known,
            hypotheses, missing, concerns, keywordMatches, summary, nextStep);
    }

    // --- Business prospect -------------------------------------------------------------------

    public static (RadarResult Result, BusinessProspectAssessment Assessment) EvaluateBusinessProspect(Opportunity opportunity, RadarPreferences preferences)
    {
        var text = $"{opportunity.Title} {opportunity.Description}".ToLowerInvariant();
        var passages = OpportunityRadarEngine.DeserializePassages(opportunity.SourcePassagesJson);
        var evidence = passages.FirstOrDefault()?.Id ?? "none";

        var strongReputation = ContainsAny(text, "years in business", "established", "5 star", "5-star", "highly rated", "well known", "well-known", "long-standing", "trusted", "loyal customers");
        var businessStrength = strongReputation ? 3 : text.Length > 200 ? 2 : 1;

        var weakDigital = ContainsAny(text, "no website", "outdated", "not mobile friendly", "not mobile-friendly", "broken", "hasn't been updated", "has not been updated", "no online booking", "no online presence");
        // Deliberately excludes bare "mobile friendly"/"mobile-friendly": those phrases are a substring
        // of "not mobile friendly", so including them here made the negative phrasing above accidentally
        // trigger this positive signal too.
        var strongDigital = ContainsAny(text, "modern website", "well-designed site", "well designed site", "recently redesigned", "responsive design", "recently launched", "newly designed");
        var digitalWeakness = weakDigital && !strongDigital ? 3 : strongDigital ? 0 : 1;

        var reputationMismatch = strongReputation && weakDigital ? 3 : 0;

        var entryProjectSignal = ContainsAny(text, "needs a new website", "no booking system", "broken contact form", "no online store", "outdated site", "could use a redesign", "redesign");
        var entryProject = entryProjectSignal ? 3 : text.Length > 200 ? 1 : 0;

        var noContact = ContainsAny(text, "no contact information", "no phone number listed", "no way to reach", "could not find contact", "no email address");
        var hasContact = ContainsAny(text, "phone number", "contact form", "email address", "listed owner", "contact page");
        var contactability = noContact ? 0 : hasContact ? 3 : 1;

        var evidenceCompleteness = text.Length >= 400 ? 3 : text.Length >= 180 ? 2 : text.Length >= 60 ? 1 : 0;

        var explicitIntent = ContainsAny(text, "requesting quotes", "requesting a quote", "posted a job for", "hiring for", "seeking a developer", "put out an rfp", "issued an rfp");
        var buyingIntent = explicitIntent ? "Strong" : "Unknown";

        var assessment = new BusinessProspectAssessment(buyingIntent, businessStrength, digitalWeakness, reputationMismatch,
            entryProject, contactability, evidenceCompleteness, evidence, evidence, evidence);
        var result = ComposeBusinessProspect(opportunity, preferences, assessment,
            ["Business strength, digital-presence weakness, and entry-project judgments are simulated heuristics for demonstration purposes."]);
        return (result, assessment);
    }

    public static RadarResult ComposeBusinessProspect(Opportunity opportunity, RadarPreferences preferences,
        BusinessProspectAssessment assessment, IReadOnlyList<string> hypotheses)
    {
        var detail = opportunity.BusinessProspectDetail
            ?? throw new InvalidOperationException("BusinessProspectDetail is required to compose a business prospect evaluation.");
        var prefs = ReadBusinessProspectPreferences(preferences);
        var industry = detail.Industry;
        var geography = detail.Geography;
        var preferredIndustry = industry is not null && prefs.PreferredIndustries.Contains(industry, StringComparer.OrdinalIgnoreCase);
        var excludedIndustry = industry is not null && prefs.ExcludedIndustries.Contains(industry, StringComparer.OrdinalIgnoreCase);
        var excludedGeography = geography is not null && prefs.ExcludedGeographies.Contains(geography, StringComparer.OrdinalIgnoreCase);
        var preferredGeography = geography is not null && prefs.PreferredGeographies.Contains(geography, StringComparer.OrdinalIgnoreCase);
        var geographyScore = excludedGeography ? 0 : preferredGeography ? 3 : geography is null ? 1 : 2;

        var businessStrength = Math.Clamp(assessment.BusinessStrengthScore, 0, 3);
        var digitalWeakness = Math.Clamp(assessment.DigitalPresenceWeaknessScore, 0, 3);
        var reputationMismatch = Math.Clamp(assessment.ReputationWebsiteMismatchScore, 0, 3);
        var entryProject = Math.Clamp(assessment.EntryProjectStrengthScore, 0, 3);
        var contactability = Math.Clamp(assessment.ContactabilityScore, 0, 3);
        var evidenceCompleteness = Math.Clamp(assessment.EvidenceCompletenessScore, 0, 3);

        var hardSkip = evidenceCompleteness == 0 || contactability == 0 || excludedIndustry || excludedGeography;

        var weights = prefs.Weights;
        var composite = businessStrength * weights.BusinessStrength + digitalWeakness * weights.DigitalPresenceWeakness
            + reputationMismatch * weights.ReputationMismatch + entryProject * weights.EntryProjectStrength
            + geographyScore * weights.Geography + contactability * weights.Contactability
            + evidenceCompleteness * weights.EvidenceCompleteness + (preferredIndustry ? 15 : 0);
        var priority = composite >= 250 ? PriorityBand.High : composite >= 170 ? PriorityBand.Medium : PriorityBand.Low;

        // BuyingIntent never gates this decision when Unknown - that is the expected default for a
        // cold prospect, not a penalty. A non-Unknown value only ever helps, surfaced under known facts.
        var recommendation = hardSkip || priority == PriorityBand.Low ? OpportunityRecommendation.Skip
            : digitalWeakness >= 2 && entryProject >= 2 && evidenceCompleteness >= 2 && contactability >= 1
                ? OpportunityRecommendation.Prioritize : OpportunityRecommendation.Watch;

        var known = new List<string>();
        if (industry is not null) known.Add($"Industry: {industry}.");
        if (geography is not null) known.Add($"Geography: {geography}.");
        if (!assessment.BuyingIntent.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            known.Add($"Buying intent evidence detected: {assessment.BuyingIntent}.");

        var missing = new List<string>();
        if (evidenceCompleteness < 2) missing.Add("The research notes need more verifiable detail before an initial review.");
        if (contactability < 2) missing.Add("A reliable way to reach a decision-maker was not confirmed.");
        if (assessment.BuyingIntent.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            missing.Add("No explicit buying-intent evidence was found. This is expected for a cold prospect and is not itself a concern.");

        var concerns = new List<string>();
        if (excludedIndustry) concerns.Add($"The industry {industry} is excluded by the current preferences.");
        if (excludedGeography) concerns.Add($"The geography {geography} is excluded by the current preferences.");
        if (contactability == 0) concerns.Add("No contact method for this business could be found.");
        if (evidenceCompleteness == 0) concerns.Add("There is not enough evidence to assess this prospect.");

        var factors = new List<RadarFactor>
        {
            new("businessStrength", "Business strength", businessStrength, assessment.ReputationEvidencePassageId,
                businessStrength >= 2 ? "The business shows real-world signals of an established, functioning operation." : "Little evidence of business strength was found."),
            new("digitalWeakness", "Digital presence weakness", digitalWeakness, assessment.ReputationEvidencePassageId,
                digitalWeakness >= 2 ? "The observable digital presence is weak relative to the business." : "The digital presence does not show a clear gap."),
            new("reputationMismatch", "Reputation vs. website mismatch", reputationMismatch, assessment.ReputationEvidencePassageId,
                reputationMismatch >= 2 ? "There is a real gap between real-world reputation and what the website conveys." : "No clear mismatch was found."),
            new("entryProject", "Entry-project strength", entryProject, assessment.EntryProjectEvidencePassageId,
                entryProject >= 2 ? "A plausible, well-scoped first engagement is visible." : "No clear entry project is visible yet."),
            new("geography", "Geography fit", geographyScore, "none",
                preferredGeography ? "This geography is a stated preference." : excludedGeography ? "This geography is excluded by preferences." : "Geography is neutral or unknown."),
            new("contactability", "Contactability", contactability, assessment.ContactEvidencePassageId,
                contactability >= 2 ? "A decision-maker appears reachable." : "Reachability is unclear or absent."),
            new("evidenceCompleteness", "Evidence completeness", evidenceCompleteness, assessment.ReputationEvidencePassageId,
                evidenceCompleteness >= 2 ? "There is enough verifiable evidence for an initial review." : "More research is needed before assessment.")
        };

        var summary = recommendation switch
        {
            OpportunityRecommendation.Prioritize => "A strong prospect: an established business with a clear digital-presence gap and a plausible entry project.",
            OpportunityRecommendation.Watch => "A plausible prospect, but not yet strong enough to prioritize outreach.",
            _ => "The current evidence does not justify outreach time here."
        };
        var nextStep = concerns.FirstOrDefault() ?? missing.FirstOrDefault() ?? "Confirm a decision-maker and the best entry project before reaching out.";

        return new RadarResult(null, industry ?? "Unknown", recommendation, priority, BudgetStatus.Unknown, factors, known,
            hypotheses, missing, concerns, [], summary, nextStep);
    }

    public static string Serialize(RadarResult result) => JsonSerializer.Serialize(result, CamelCaseOptions());
    public static string Serialize(ActiveProjectAssessment assessment) => JsonSerializer.Serialize(assessment, CamelCaseOptions());
    public static string Serialize(BusinessProspectAssessment assessment) => JsonSerializer.Serialize(assessment, CamelCaseOptions());

    public static ActiveProjectAssessment? DeserializeActiveProjectAssessment(string json) =>
        JsonSerializer.Deserialize<ActiveProjectAssessment>(json, JsonOptions());
    public static BusinessProspectAssessment? DeserializeBusinessProspectAssessment(string json) =>
        JsonSerializer.Deserialize<BusinessProspectAssessment>(json, JsonOptions());

    public static ActiveProjectPreferences ReadActiveProjectPreferences(RadarPreferences preferences)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ActiveProjectPreferencesJson>(preferences.ActiveProjectPreferencesJson, JsonOptions());
            return new ActiveProjectPreferences(
                parsed?.Capabilities ?? [], parsed?.PreferredProjectTypes ?? [], parsed?.ExcludedProjectTypes ?? [],
                parsed?.MinimumBudget ?? 2500m, parsed?.MinimumWeeks ?? 2, parsed?.MaximumWeeks ?? 12,
                Enum.TryParse<IncompleteInformationTolerance>(parsed?.IncompleteInformationTolerance, true, out var tolerance) ? tolerance : IncompleteInformationTolerance.Medium,
                NormalizeWeights(new ActiveProjectWeights(ReadWeight(parsed?.Weights, "capabilityFit", 35), ReadWeight(parsed?.Weights, "problemClarity", 30),
                    ReadWeight(parsed?.Weights, "independentScope", 20), ReadWeight(parsed?.Weights, "informationSufficiency", 15))));
        }
        catch (JsonException)
        {
            return new ActiveProjectPreferences([], [], [], 2500m, 2, 12, IncompleteInformationTolerance.Medium,
                NormalizeWeights(new ActiveProjectWeights(35, 30, 20, 15)));
        }
    }

    public static BusinessProspectPreferences ReadBusinessProspectPreferences(RadarPreferences preferences)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BusinessProspectPreferencesJson>(preferences.BusinessProspectPreferencesJson, JsonOptions());
            return new BusinessProspectPreferences(
                parsed?.PreferredIndustries ?? [], parsed?.ExcludedIndustries ?? [],
                parsed?.PreferredGeographies ?? [], parsed?.ExcludedGeographies ?? [],
                NormalizeWeights(new BusinessProspectWeights(
                    ReadWeight(parsed?.Weights, "businessStrength", 15), ReadWeight(parsed?.Weights, "digitalPresenceWeakness", 25),
                    ReadWeight(parsed?.Weights, "reputationMismatch", 15), ReadWeight(parsed?.Weights, "entryProjectStrength", 20),
                    ReadWeight(parsed?.Weights, "geography", 5), ReadWeight(parsed?.Weights, "contactability", 10),
                    ReadWeight(parsed?.Weights, "evidenceCompleteness", 10))));
        }
        catch (JsonException)
        {
            return new BusinessProspectPreferences([], [], [], [],
                NormalizeWeights(new BusinessProspectWeights(15, 25, 15, 20, 5, 10, 10)));
        }
    }

    // Record equality on ActiveProjectPreferences/BusinessProspectPreferences falls back to reference
    // equality for their string[] fields, so two freshly-deserialized instances with identical content
    // are never == to each other. Compare structurally instead; Weights is safe to compare directly
    // since it's a readonly record struct of ints.
    public static bool ActiveProjectPreferencesEqual(ActiveProjectPreferences a, ActiveProjectPreferences b) =>
        SetsEqual(a.Capabilities, b.Capabilities) && SetsEqual(a.PreferredProjectTypes, b.PreferredProjectTypes)
        && SetsEqual(a.ExcludedProjectTypes, b.ExcludedProjectTypes) && a.MinimumBudget == b.MinimumBudget
        && a.MinimumWeeks == b.MinimumWeeks && a.MaximumWeeks == b.MaximumWeeks
        && a.IncompleteInformationTolerance == b.IncompleteInformationTolerance && a.Weights == b.Weights;

    public static bool BusinessProspectPreferencesEqual(BusinessProspectPreferences a, BusinessProspectPreferences b) =>
        SetsEqual(a.PreferredIndustries, b.PreferredIndustries) && SetsEqual(a.ExcludedIndustries, b.ExcludedIndustries)
        && SetsEqual(a.PreferredGeographies, b.PreferredGeographies) && SetsEqual(a.ExcludedGeographies, b.ExcludedGeographies)
        && a.Weights == b.Weights;

    private static bool SetsEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(right.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    // Admin-entered weights are not required to sum to 100, but the PriorityBand thresholds in
    // ComposeActiveProject/ComposeBusinessProspect are calibrated assuming they do. Normalizing here,
    // at the single read site every caller goes through, keeps those thresholds meaningful regardless
    // of what raw values were entered (e.g. every weight set to 1 no longer collapses ranking to Low).
    private static ActiveProjectWeights NormalizeWeights(ActiveProjectWeights weights)
    {
        var sum = weights.CapabilityFit + weights.ProblemClarity + weights.IndependentScope + weights.InformationSufficiency;
        if (sum <= 0) return weights;
        var scale = 100m / sum;
        return new ActiveProjectWeights((int)Math.Round(weights.CapabilityFit * scale), (int)Math.Round(weights.ProblemClarity * scale),
            (int)Math.Round(weights.IndependentScope * scale), (int)Math.Round(weights.InformationSufficiency * scale));
    }

    private static BusinessProspectWeights NormalizeWeights(BusinessProspectWeights weights)
    {
        var sum = weights.BusinessStrength + weights.DigitalPresenceWeakness + weights.ReputationMismatch
            + weights.EntryProjectStrength + weights.Geography + weights.Contactability + weights.EvidenceCompleteness;
        if (sum <= 0) return weights;
        var scale = 100m / sum;
        return new BusinessProspectWeights((int)Math.Round(weights.BusinessStrength * scale), (int)Math.Round(weights.DigitalPresenceWeakness * scale),
            (int)Math.Round(weights.ReputationMismatch * scale), (int)Math.Round(weights.EntryProjectStrength * scale),
            (int)Math.Round(weights.Geography * scale), (int)Math.Round(weights.Contactability * scale),
            (int)Math.Round(weights.EvidenceCompleteness * scale));
    }

    private sealed record ActiveProjectPreferencesJson(string[]? Capabilities, string[]? PreferredProjectTypes, string[]? ExcludedProjectTypes,
        decimal? MinimumBudget, int? MinimumWeeks, int? MaximumWeeks, string? IncompleteInformationTolerance, Dictionary<string, int>? Weights);
    private sealed record BusinessProspectPreferencesJson(string[]? PreferredIndustries, string[]? ExcludedIndustries,
        string[]? PreferredGeographies, string[]? ExcludedGeographies, Dictionary<string, int>? Weights);

    private static int ReadWeight(Dictionary<string, int>? weights, string key, int fallback) =>
        weights is not null && weights.TryGetValue(key, out var value) ? Math.Clamp(value, 0, 100) : fallback;

    private static BudgetStatus ParseBudgetStatus(string text, decimal floor, out decimal? stated)
    {
        stated = null;
        var match = BudgetRegex().Match(text);
        if (!match.Success) return BudgetStatus.Unknown;
        if (!decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return BudgetStatus.Unknown;
        if (match.Groups[2].Value.Equals("k", StringComparison.OrdinalIgnoreCase)) value *= 1000;
        stated = value;
        return value < floor ? BudgetStatus.Incompatible : BudgetStatus.Compatible;
    }

    private static string ClassifyProjectType(string text)
    {
        if (ContainsAny(text, "integrat", "connect", "sync")) return "Integration";
        if (ContainsAny(text, "automat", "workflow", "rekey")) return "Automation";
        if (ContainsAny(text, "report", "dashboard")) return "Reporting";
        if (ContainsAny(text, "portal")) return "Portal";
        if (ContainsAny(text, "internal tool", "back office")) return "InternalTool";
        return "Other";
    }

    private static bool ContainsAny(string text, params string[] values) => values.Any(text.Contains);

    private static double? ParseDurationWeeks(string text)
    {
        var match = NumericDurationRegex().Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return null;
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "day" or "days" => value / 7d,
            "month" or "months" => value * 4.345d,
            _ => value
        };
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };
    private static JsonSerializerOptions CamelCaseOptions() => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [GeneratedRegex(@"\$\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*([kK]?)", RegexOptions.CultureInvariant)]
    private static partial Regex BudgetRegex();
    [GeneratedRegex(@"\b(?:week|weeks|month|months|day|days|timeline|deadline)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();
    [GeneratedRegex(@"\b([0-9]+(?:\.[0-9]+)?)\s*(day|days|week|weeks|month|months)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericDurationRegex();
}
