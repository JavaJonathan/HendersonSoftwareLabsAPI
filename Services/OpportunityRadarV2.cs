using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public record JevJudgment(double Score, double Confidence, string EvidencePassageId);

public record ActiveProjectV2Assessment(
    string Kind,
    double KindConfidence,
    string ProjectType,
    IReadOnlyDictionary<string, JevJudgment> Factors,
    bool EmploymentOrStaffing,
    bool TeamScale,
    bool CoreSystemReplacement,
    string ConcernEvidencePassageId);

public record BusinessProspectV2Assessment(
    BusinessProspectType ProspectType,
    double ProspectTypeConfidence,
    IReadOnlyDictionary<string, JevJudgment> Factors,
    bool SpeculativeWorkflow,
    bool PhysicalOrJudgmentHeavy,
    bool CoreSystemReplacement,
    string ConcernEvidencePassageId);

public static partial class OpportunityRadarV2
{
    public const string ActiveRubricVersion = "radar-active-project-v2";
    public const string OperationalRubricVersion = "radar-operational-pain-v2";
    public const string DigitalRubricVersion = "radar-digital-presence-v2";

    public static readonly IReadOnlyDictionary<string, decimal> ActiveDefaults = new Dictionary<string, decimal>
    {
        ["problemClarity"] = 20, ["hslDeliveryFit"] = 25, ["independentScope"] = 20,
        ["economicViability"] = 15, ["urgency"] = 10, ["buyerReadiness"] = 5, ["informationMarketFit"] = 5
    };

    public static readonly IReadOnlyDictionary<string, decimal> OperationalDefaults = new Dictionary<string, decimal>
    {
        ["painEvidence"] = 20, ["automationFeasibility"] = 20, ["economicLeverage"] = 15,
        ["containedEngagement"] = 15, ["urgency"] = 10, ["hslDeliveryFit"] = 10,
        ["buyerAccess"] = 5, ["marketAccessFit"] = 5
    };

    public static readonly IReadOnlyDictionary<string, decimal> DigitalDefaults = new Dictionary<string, decimal>
    {
        ["businessStrength"] = 15, ["digitalWeakness"] = 20, ["reputationMismatch"] = 15,
        ["entryProjectStrength"] = 20, ["urgency"] = 10, ["hslDeliveryFit"] = 10,
        ["buyerAccess"] = 5, ["marketAccessFit"] = 5
    };

    public static RadarResult ComposeActiveProject(Opportunity opportunity, RadarPreferences preferences, ActiveProjectV2Assessment assessment)
    {
        var prefs = OpportunityRadarEngine.ReadActiveProjectPreferences(preferences);
        var weights = ReadWeights(preferences.ActiveProjectPreferencesJson, "weightsV2", ActiveDefaults);
        var factors = new Dictionary<string, JevJudgment>(assessment.Factors, StringComparer.OrdinalIgnoreCase)
        {
            ["informationMarketFit"] = assessment.Factors.TryGetValue("informationMarketFit", out var information)
                ? information : new JevJudgment(1.5, 0.75, "none")
        };
        var checks = new List<EvaluationCheck>();
        if (assessment.EmploymentOrStaffing)
            checks.Add(new("employment", EvaluationCheckSeverity.Block, "The source describes employment or staffing rather than an independent project.", assessment.ConcernEvidencePassageId));
        if (assessment.TeamScale)
            checks.Add(new("teamScale", EvaluationCheckSeverity.Review, "The requested work appears to require a team-scale or unusually broad engagement.", assessment.ConcernEvidencePassageId));
        if (assessment.CoreSystemReplacement)
            checks.Add(new("coreReplacement", EvaluationCheckSeverity.Review, "The work may require replacing a specialized core system instead of complementing it.", assessment.ConcernEvidencePassageId));
        if (assessment.Kind.Equals("Other", StringComparison.OrdinalIgnoreCase) || assessment.KindConfidence < 0.55)
            checks.Add(new("weakClassification", EvaluationCheckSeverity.Review, "Jev's demand classification is unknown or below 55% confidence.", Category: EvaluationCheckCategory.EvidenceGap));
        if (prefs.ExcludedProjectTypes.Contains(assessment.ProjectType, StringComparer.OrdinalIgnoreCase))
            checks.Add(new("excludedProjectType", EvaluationCheckSeverity.Block, $"{assessment.ProjectType} is excluded by the current screening preferences."));
        var budget = ParseBudget($"{opportunity.Title} {opportunity.Description}", prefs.MinimumBudget);
        if (budget == BudgetStatus.Incompatible)
            checks.Add(new("inadequateBudget", EvaluationCheckSeverity.Block, "The stated budget is below the configured minimum."));
        AddCommonChecks(opportunity, factors, checks, ActiveLabels);

        var score = WeightedScore(factors, weights);
        var factorConfidence = WeightedConfidence(factors, weights);
        var confidence = decimal.Round((decimal)(assessment.KindConfidence * 0.2) + factorConfidence * 0.8m, 4);
        AddConfidenceChecks(confidence, factors, checks, ActiveLabels);
        EnsureCheck(checks);
        var (priority, needsVerification, recommendation) = Decide(score, checks,
            OpportunityRecommendation.Pass, OpportunityRecommendation.Pursue, OpportunityRecommendation.Investigate);
        var radarFactors = ToRadarFactors(factors, ActiveLabels);
        var (concerns, missingInformation) = SplitChecks(checks);
        var summary = recommendation switch
        {
            OpportunityRecommendation.Pursue => "A strong, actionable project opportunity with supported HSL fit.",
            OpportunityRecommendation.Investigate => "The opportunity may be worthwhile, but specific risks or evidence gaps need verification.",
            _ => "The current evidence or deterministic checks do not support pursuing this project."
        };
        return new RadarResult(assessment.Kind, assessment.ProjectType, recommendation, priority, budget, radarFactors,
            ["Jev supplied the semantic judgments; application code applied the configured scoring profile."],
            missingInformation, concerns, summary,
            concerns.FirstOrDefault() ?? missingInformation.FirstOrDefault() ?? "Confirm the buyer, scope, and delivery path.", score, confidence, null,
            needsVerification, checks, weights, ActiveRubricVersion);
    }

    public static RadarResult ComposeBusinessProspect(Opportunity opportunity, RadarPreferences preferences, BusinessProspectV2Assessment assessment)
    {
        var detail = opportunity.BusinessProspectDetail ?? throw new InvalidOperationException("BusinessProspectDetail is required.");
        var prefs = OpportunityRadarEngine.ReadBusinessProspectPreferences(preferences);
        var resolvedType = detail.ProspectTypeOverride
            ?? (assessment.ProspectType != BusinessProspectType.Unknown
                ? assessment.ProspectType
                : detail.ImportedProspectType ?? BusinessProspectType.Unknown);
        var digital = resolvedType == BusinessProspectType.DigitalPresence;
        var defaults = digital ? DigitalDefaults : OperationalDefaults;
        var weightsKey = digital ? "digitalPresenceWeights" : "operationalPainWeights";
        var weights = ReadWeights(preferences.BusinessProspectPreferencesJson, weightsKey, defaults);
        var factors = new Dictionary<string, JevJudgment>(assessment.Factors, StringComparer.OrdinalIgnoreCase)
        {
            ["marketAccessFit"] = new JevJudgment(MarketFit(detail, prefs), 1, "none")
        };
        var checks = new List<EvaluationCheck>();
        if (detail.ImportedProspectType is { } imported && imported != BusinessProspectType.Unknown
            && assessment.ProspectType != BusinessProspectType.Unknown && imported != assessment.ProspectType)
            checks.Add(new("typeDisagreement", detail.ProspectTypeOverride is null ? EvaluationCheckSeverity.Review : EvaluationCheckSeverity.Info,
                $"The sourcing agent classified this as {Label(imported)}, while Jev classified it as {Label(assessment.ProspectType)}.", Category: EvaluationCheckCategory.EvidenceGap));
        if (assessment.ProspectType == BusinessProspectType.Unknown)
            checks.Add(new("unknownType", EvaluationCheckSeverity.Review, "Jev could not determine a supported prospect type from the available evidence.", Category: EvaluationCheckCategory.EvidenceGap));
        if (assessment.ProspectTypeConfidence < 0.55)
            checks.Add(new("weakClassification", EvaluationCheckSeverity.Review, "Jev's prospect classification confidence is below 55%.", Category: EvaluationCheckCategory.EvidenceGap));
        if (assessment.SpeculativeWorkflow)
            checks.Add(new("speculativeWorkflow", EvaluationCheckSeverity.Review, "The workflow claim appears to rely mainly on industry assumptions rather than direct evidence.", assessment.ConcernEvidencePassageId));
        if (assessment.PhysicalOrJudgmentHeavy)
            checks.Add(new("physicalOrJudgmentHeavy", EvaluationCheckSeverity.Review, "The work appears substantially physical, relationship-based, judgment-heavy, or exception-heavy.", assessment.ConcernEvidencePassageId));
        if (assessment.CoreSystemReplacement)
            checks.Add(new("coreReplacement", EvaluationCheckSeverity.Review, "The proposed solution may require replacing a specialized core system.", assessment.ConcernEvidencePassageId));
        if (detail.Industry is not null && prefs.ExcludedIndustries.Contains(detail.Industry, StringComparer.OrdinalIgnoreCase))
            checks.Add(new("excludedIndustry", EvaluationCheckSeverity.Block, $"{detail.Industry} is excluded by the current screening preferences."));
        if (detail.Geography is not null && prefs.ExcludedGeographies.Contains(detail.Geography, StringComparer.OrdinalIgnoreCase))
            checks.Add(new("excludedGeography", EvaluationCheckSeverity.Block, $"{detail.Geography} is excluded by the current screening preferences."));
        var labels = digital ? DigitalLabels : OperationalLabels;
        var scoringFactors = factors.Where(x => weights.ContainsKey(x.Key)).ToDictionary();
        AddCommonChecks(opportunity, scoringFactors, checks, labels);

        var score = resolvedType == BusinessProspectType.Unknown ? (decimal?)null : WeightedScore(scoringFactors, weights);
        var factorConfidence = WeightedConfidence(scoringFactors, weights);
        var confidence = decimal.Round((decimal)(assessment.ProspectTypeConfidence * 0.2) + factorConfidence * 0.8m, 4);
        AddConfidenceChecks(confidence, scoringFactors, checks, labels);
        if (score is null)
            checks.Add(new("insufficientEvidence", EvaluationCheckSeverity.Review, "There is not enough evidence to calculate a defensible opportunity score.", Category: EvaluationCheckCategory.EvidenceGap));
        EnsureCheck(checks);
        var (priority, needsVerification, recommendation) = Decide(score, checks,
            OpportunityRecommendation.Skip, OpportunityRecommendation.Prioritize, OpportunityRecommendation.Watch);
        var radarFactors = ToRadarFactors(scoringFactors, labels);
        var (concerns, missingInformation) = SplitChecks(checks);
        var summary = recommendation switch
        {
            OpportunityRecommendation.Prioritize => $"A strong {Label(resolvedType).ToLowerInvariant()} prospect with an actionable first engagement.",
            OpportunityRecommendation.Watch => "The underlying opportunity may be attractive, but its evidence or delivery path needs verification.",
            _ => "The current evidence or deterministic checks do not justify prioritizing outreach."
        };
        return new RadarResult(resolvedType.ToString(), detail.Industry ?? "Unknown", recommendation, priority, BudgetStatus.Unknown,
            radarFactors, ["Jev evaluated the evidence independently from the sourcing agent's classification and confidence."],
            missingInformation, concerns, summary,
            concerns.FirstOrDefault() ?? missingInformation.FirstOrDefault() ?? "Confirm the buyer and the smallest valuable first engagement.", score, confidence,
            resolvedType, needsVerification, checks, weights, digital ? DigitalRubricVersion : OperationalRubricVersion);
    }

    public static string Serialize(ActiveProjectV2Assessment value) => JsonSerializer.Serialize(value, OpportunityRadarEngine.CamelCaseOptions);
    public static string Serialize(BusinessProspectV2Assessment value) => JsonSerializer.Serialize(value, OpportunityRadarEngine.CamelCaseOptions);

    public static ActiveProjectV2Assessment? DeserializeActive(string json)
    {
        var value = JsonSerializer.Deserialize<ActiveProjectV2Assessment>(json, OpportunityRadarEngine.CaseInsensitiveOptions);
        // Pre-v2 AssessmentJson has no "factors" property and binds it to null instead of failing to
        // deserialize, so this checks the shape explicitly rather than letting a stale row crash the
        // caller (e.g. a legacy Ready row recomposed before AddOpportunityRadarJevV2 has been applied).
        return value?.Factors is null ? null : value;
    }

    public static BusinessProspectV2Assessment? DeserializeBusiness(string json)
    {
        var value = JsonSerializer.Deserialize<BusinessProspectV2Assessment>(json, OpportunityRadarEngine.CaseInsensitiveOptions);
        return value?.Factors is null ? null : value;
    }

    public static IReadOnlyDictionary<string, decimal> ReadActiveWeights(RadarPreferences preferences) =>
        ReadWeights(preferences.ActiveProjectPreferencesJson, "weightsV2", ActiveDefaults);

    public static IReadOnlyDictionary<string, decimal> ReadOperationalWeights(RadarPreferences preferences) =>
        ReadWeights(preferences.BusinessProspectPreferencesJson, "operationalPainWeights", OperationalDefaults);

    public static IReadOnlyDictionary<string, decimal> ReadDigitalWeights(RadarPreferences preferences) =>
        ReadWeights(preferences.BusinessProspectPreferencesJson, "digitalPresenceWeights", DigitalDefaults);

    private static IReadOnlyDictionary<string, decimal> ReadWeights(string json, string property, IReadOnlyDictionary<string, decimal> defaults)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object)
                return Normalize(defaults, defaults);
            var values = defaults.Keys.ToDictionary(key => key, key =>
                element.TryGetProperty(key, out var value) && value.TryGetDecimal(out var parsed) ? Math.Clamp(parsed, 0, 100) : defaults[key]);
            return Normalize(values, defaults);
        }
        catch (JsonException) { return Normalize(defaults, defaults); }
    }

    private static IReadOnlyDictionary<string, decimal> Normalize(IReadOnlyDictionary<string, decimal> values, IReadOnlyDictionary<string, decimal> defaults)
    {
        var source = values.Values.Sum() <= 0 ? defaults : values;
        var sum = source.Values.Sum();
        return source.ToDictionary(x => x.Key, x => decimal.Round(x.Value * 100m / sum, 4));
    }

    private static decimal WeightedScore(IReadOnlyDictionary<string, JevJudgment> factors, IReadOnlyDictionary<string, decimal> weights)
    {
        var value = weights.Sum(weight => Math.Clamp((decimal)factors[weight.Key].Score, 0, 3) / 3m * weight.Value);
        return decimal.Round(value, 2);
    }

    private static decimal WeightedConfidence(IReadOnlyDictionary<string, JevJudgment> factors, IReadOnlyDictionary<string, decimal> weights)
    {
        var value = weights.Sum(weight => Math.Clamp((decimal)factors[weight.Key].Confidence, 0, 1) * weight.Value) / 100m;
        return decimal.Round(value, 4);
    }

    private static void AddCommonChecks(Opportunity opportunity, IReadOnlyDictionary<string, JevJudgment> factors, List<EvaluationCheck> checks, IReadOnlyDictionary<string, string> labels)
    {
        if (opportunity.ResearchConfidence == ResearchConfidence.Low)
            checks.Add(new("lowResearchConfidence", EvaluationCheckSeverity.Review, "The sourcing agent marked the underlying research confidence as Low.", Category: EvaluationCheckCategory.EvidenceGap));
        if (opportunity.SourceDate is { } date && date < DateTime.UtcNow.AddDays(-180))
            checks.Add(new("staleEvidence", EvaluationCheckSeverity.Review, "The primary evidence source is more than 180 days old.", Category: EvaluationCheckCategory.EvidenceGap));
        foreach (var factor in factors.Where(x => x.Value.Score >= 2 && x.Value.EvidencePassageId == "none"))
            checks.Add(new($"unsupported:{factor.Key}", EvaluationCheckSeverity.Review, $"The high {labels.GetValueOrDefault(factor.Key, factor.Key)} judgment has no selected supporting passage.", Category: EvaluationCheckCategory.EvidenceGap));
    }

    private static void AddConfidenceChecks(decimal confidence, IReadOnlyDictionary<string, JevJudgment> factors, List<EvaluationCheck> checks, IReadOnlyDictionary<string, string> labels)
    {
        if (confidence < 0.65m)
            checks.Add(new("lowJevConfidence", EvaluationCheckSeverity.Review, "Overall Jev confidence is below 65%.", Category: EvaluationCheckCategory.EvidenceGap));
        foreach (var factor in factors.Where(x => x.Value.Confidence < 0.55))
            checks.Add(new($"lowConfidence:{factor.Key}", EvaluationCheckSeverity.Review, $"Jev confidence for {labels.GetValueOrDefault(factor.Key, factor.Key)} is below 55%.", factor.Value.EvidencePassageId, EvaluationCheckCategory.EvidenceGap));
    }

    private static void EnsureCheck(List<EvaluationCheck> checks)
    {
        if (checks.Count == 0) checks.Add(new("clear", EvaluationCheckSeverity.Info, "No deterministic review or blocking checks were triggered."));
    }

    // Shared by both Compose* methods: priority/verification/recommendation only ever differ in which
    // three OpportunityRecommendation values apply, so the caller supplies those and everything else
    // (the block/priority/verification rule) lives in exactly one place.
    private static (PriorityBand Priority, bool NeedsVerification, OpportunityRecommendation Recommendation) Decide(
        decimal? score, List<EvaluationCheck> checks,
        OpportunityRecommendation reject, OpportunityRecommendation strong, OpportunityRecommendation middle)
    {
        var priority = score is null ? PriorityBand.Low : Priority(score.Value);
        var needsVerification = checks.Any(x => x.Severity == EvaluationCheckSeverity.Review);
        var hasBlock = checks.Any(x => x.Severity == EvaluationCheckSeverity.Block);
        var recommendation = hasBlock || score is null || priority == PriorityBand.Low ? reject
            : priority == PriorityBand.High && !needsVerification ? strong
            : middle;
        return (priority, needsVerification, recommendation);
    }

    // Missing information is specifically an evidence or confidence gap (EvaluationCheckCategory.EvidenceGap,
    // set where each check is created); everything else non-Info is a substantive concern. These previously
    // fed the same list, which made the two RadarResult sections display identical text whenever a record
    // needed verification.
    private static (List<string> Concerns, List<string> MissingInformation) SplitChecks(List<EvaluationCheck> checks)
    {
        var nonInfo = checks.Where(x => x.Severity != EvaluationCheckSeverity.Info).ToList();
        return (nonInfo.Where(x => x.Category != EvaluationCheckCategory.EvidenceGap).Select(x => x.Explanation).ToList(),
            nonInfo.Where(x => x.Category == EvaluationCheckCategory.EvidenceGap).Select(x => x.Explanation).ToList());
    }

    private static List<RadarFactor> ToRadarFactors(IReadOnlyDictionary<string, JevJudgment> factors, IReadOnlyDictionary<string, string> labels) =>
        factors.Select(x => new RadarFactor(x.Key, labels.GetValueOrDefault(x.Key, x.Key), Math.Round(x.Value.Score / 3d * 100d, 1),
            x.Value.EvidencePassageId, $"Jev scored this factor at {Math.Round(x.Value.Score, 2)} out of 3 with {Math.Round(x.Value.Confidence * 100)}% confidence.")).ToList();

    private static PriorityBand Priority(decimal score) => score >= 75 ? PriorityBand.High : score >= 55 ? PriorityBand.Medium : PriorityBand.Low;

    private static double MarketFit(BusinessProspectDetail detail, BusinessProspectPreferences prefs)
    {
        if (detail.Industry is not null && prefs.ExcludedIndustries.Contains(detail.Industry, StringComparer.OrdinalIgnoreCase)) return 0;
        if (detail.Geography is not null && prefs.ExcludedGeographies.Contains(detail.Geography, StringComparer.OrdinalIgnoreCase)) return 0;
        if (detail.Industry is not null && prefs.PreferredIndustries.Contains(detail.Industry, StringComparer.OrdinalIgnoreCase)) return 3;
        if (detail.Geography is not null && prefs.PreferredGeographies.Contains(detail.Geography, StringComparer.OrdinalIgnoreCase)) return 3;
        return detail.Industry is null && detail.Geography is null ? 1 : 2;
    }

    private static BudgetStatus ParseBudget(string text, decimal floor)
    {
        var match = BudgetRegex().Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return BudgetStatus.Unknown;
        if (match.Groups[2].Value.Equals("k", StringComparison.OrdinalIgnoreCase)) value *= 1000;
        return value < floor ? BudgetStatus.Incompatible : BudgetStatus.Compatible;
    }

    private static string Label(BusinessProspectType value) => value switch
    {
        BusinessProspectType.OperationalPain => "Operational Pain",
        BusinessProspectType.DigitalPresence => "Digital Presence",
        _ => value.ToString()
    };

    private static readonly IReadOnlyDictionary<string, string> ActiveLabels = new Dictionary<string, string>
    {
        ["problemClarity"] = "Problem clarity", ["hslDeliveryFit"] = "HSL delivery fit", ["independentScope"] = "Independent scope",
        ["economicViability"] = "Economic viability", ["urgency"] = "Urgency", ["buyerReadiness"] = "Buyer readiness",
        ["informationMarketFit"] = "Information and market fit"
    };

    private static readonly IReadOnlyDictionary<string, string> OperationalLabels = new Dictionary<string, string>
    {
        ["painEvidence"] = "Observed pain evidence", ["automationFeasibility"] = "Automation feasibility",
        ["economicLeverage"] = "Economic leverage", ["containedEngagement"] = "Contained first engagement",
        ["urgency"] = "Urgency", ["hslDeliveryFit"] = "HSL delivery fit", ["buyerAccess"] = "Buyer access",
        ["marketAccessFit"] = "Market and local-access fit"
    };

    private static readonly IReadOnlyDictionary<string, string> DigitalLabels = new Dictionary<string, string>
    {
        ["businessStrength"] = "Business strength", ["digitalWeakness"] = "Digital presence weakness",
        ["reputationMismatch"] = "Reputation mismatch", ["entryProjectStrength"] = "Entry-project strength",
        ["urgency"] = "Urgency", ["hslDeliveryFit"] = "HSL delivery fit", ["buyerAccess"] = "Buyer access",
        ["marketAccessFit"] = "Market and local-access fit"
    };

    [GeneratedRegex(@"\$\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*([kK]?)", RegexOptions.CultureInvariant)]
    private static partial Regex BudgetRegex();
}
