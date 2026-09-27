using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Controllers;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) failures.Add(message); }

RadarPreferences MakePreferences(
    object? active = null,
    object? operational = null,
    object? digital = null,
    string[]? excludedIndustries = null) => new()
{
    ActiveProjectPreferencesJson = JsonSerializer.Serialize(new
    {
        capabilities = new[] { ".NET", "React", "SQL", "REST APIs" },
        preferredProjectTypes = Array.Empty<string>(), excludedProjectTypes = Array.Empty<string>(),
        minimumBudget = 2500,
        incompleteInformationTolerance = "Medium", weightsV2 = active ?? OpportunityRadarV2.ActiveDefaults
    }),
    BusinessProspectPreferencesJson = JsonSerializer.Serialize(new
    {
        preferredIndustries = Array.Empty<string>(), excludedIndustries = excludedIndustries ?? Array.Empty<string>(),
        preferredGeographies = Array.Empty<string>(), excludedGeographies = Array.Empty<string>(),
        operationalPainWeights = operational ?? OpportunityRadarV2.OperationalDefaults,
        digitalPresenceWeights = digital ?? OpportunityRadarV2.DigitalDefaults
    }),
    DigestActiveProjectCount = 3,
    DigestBusinessProspectCount = 2
};

Opportunity MakeActiveProject(string title, string request, string? budget = null) => new()
{
    EntityType = OpportunityEntityType.ActiveProject,
    Title = title,
    Description = request,
    SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.BuildActiveProjectPassages(request, budget, null)),
    ActiveProjectDetail = new ActiveProjectDetail { DeclaredSourceType = ActiveProjectSourceType.ExplicitDemand, Budget = budget }
};

Opportunity MakeBusinessProspect(string title, string evidenceText, string? industry = null, string? geography = null) => new()
{
    EntityType = OpportunityEntityType.BusinessProspect,
    Title = title,
    Description = evidenceText,
    SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.BuildBusinessProspectPassages([new EvidenceFact(evidenceText, null, null)])),
    BusinessProspectDetail = new BusinessProspectDetail
    {
        NormalizedBusinessName = OpportunityRadarEngine.NormalizeBusinessName(title),
        Industry = industry,
        Geography = geography
    }
};

var preferences = MakePreferences();

// Structured evidence: passages come directly from the import contract, one per fixed field
// (Active Project) or one per fact (Business Prospect), with no mechanical text-chopping.
var activeProjectPassages = OpportunityRadarEngine.BuildActiveProjectPassages(
    "Reconcile orders daily.", "$5,000", new CompetitionInfo("5-10", 1, 0));
Check(activeProjectPassages.Select(x => x.Id).SequenceEqual(["request", "budget", "competition"]),
    "Active Project passages must be named after their source field, in a stable order.");
Check(activeProjectPassages[0].Text == "Reconcile orders daily." && activeProjectPassages[1].Text == "$5,000"
    && activeProjectPassages[2].Text == "Proposals: 5-10, Interviewing: 1, Hires: 0",
    "Active Project passage text must be verbatim, not mechanically chopped.");
Check(OpportunityRadarEngine.BuildActiveProjectPassages("Just the request.", null, null).Count == 1,
    "Budget and competition passages must be omitted when not supplied.");

var facts = new EvidenceFact[]
{
    new("A public job listing describes manual order handling.", "https://example.com/job", new DateTime(2026, 1, 5)),
    new("A review site shows a four-star average.", null, null)
};
var businessProspectPassages = OpportunityRadarEngine.BuildBusinessProspectPassages(facts);
Check(businessProspectPassages.Select(x => x.Id).SequenceEqual(["fact-1", "fact-2"])
    && businessProspectPassages[0].Source == "https://example.com/job" && businessProspectPassages[0].Date == new DateTime(2026, 1, 5)
    && businessProspectPassages[1].Source is null,
    "Business Prospect passages must carry each fact's own source and date.");

var activeDescription = OpportunityRadarEngine.ComposeActiveProjectDescription(new ActiveProjectImportRequest(
    "Title", "The request text.", Budget: "$5,000", Risk: "Scope could grow."));
Check(activeDescription.Contains("Request:\nThe request text.") && activeDescription.Contains("Budget:\n$5,000")
    && activeDescription.Contains("Risk:\nScope could grow."),
    "The derived Active Project description must include every populated field under its own label.");

var businessDescription = OpportunityRadarEngine.ComposeBusinessProspectDescription(new BusinessProspectImportRequest(
    "Business", facts, Fit: "A contained first engagement is plausible."));
Check(businessDescription.Contains("Fit:\nA contained first engagement is plausible.") && businessDescription.Contains("- A public job listing"),
    "The derived Business Prospect description must include the fit assessment and the evidence facts.");

// Evidence array validation: the combined fact length must land in the same 20-30,000 character
// budget the old single evidence string enforced, now checked via IValidatableObject since it spans
// multiple items instead of one [StringLength] attribute.
var validationResults = new List<ValidationResult>();
var thinRequest = new BusinessProspectImportRequest("Business", [new EvidenceFact("Too short.", null, null)]);
Check(!Validator.TryValidateObject(thinRequest, new ValidationContext(thinRequest), validationResults, true),
    "Combined evidence under 20 characters must fail validation.");
validationResults.Clear();
var validRequest = new BusinessProspectImportRequest("Business", facts);
Check(Validator.TryValidateObject(validRequest, new ValidationContext(validRequest), validationResults, true),
    "Combined evidence within budget must pass validation.");

// Budget now comes from an explicit field instead of a regex over free text.
var budgetOpportunity = MakeActiveProject("Budget test", "A contained integration project.", "$1,000");
var budgetPreferences = MakePreferences();
var budgetAssessment = new ActiveProjectV2Assessment("ExplicitDemand", 0.9, "Automation",
    OpportunityRadarV2.ActiveDefaults.Keys.ToDictionary(key => key, _ => new JevJudgment(2.4, 0.8, "request")), false, false, false, "none");
Check(OpportunityRadarV2.ComposeActiveProject(budgetOpportunity, budgetPreferences, budgetAssessment).BudgetStatus == BudgetStatus.Incompatible,
    "A stated budget below the configured minimum must resolve to Incompatible from the explicit Budget field.");

Check(OpportunityRadarEngine.Fingerprint("A", "B", null) == OpportunityRadarEngine.Fingerprint("a", "b", null),
    "Fingerprint normalization should be case-insensitive.");
Check(OpportunityRadarEngine.NormalizeWebsiteDomain("https://www.Example-Site.test/path") == "example-site.test",
    "Website domain normalization should lowercase and strip www.");
Check(OpportunityRadarEngine.NormalizeWebsiteDomain(null) == "" && OpportunityRadarEngine.NormalizeWebsiteDomain("not a url") == "",
    "Website domain normalization should fail safely.");

// Jev v2 deterministic composition and editable profiles.
var activeJudgments = OpportunityRadarV2.ActiveDefaults.Keys.ToDictionary(
    key => key, _ => new JevJudgment(2.4, 0.8, "p1"));
var activeOpportunity = MakeActiveProject("V2 project", "A specific workflow project with a stated budget of $8,000 and a contained eight week delivery.");
var activeAssessment = new ActiveProjectV2Assessment("ExplicitDemand", 0.9, "Automation", activeJudgments, false, false, false, "none");
var weightsA = MakePreferences(active: new { problemClarity = 20, hslDeliveryFit = 25, independentScope = 20, economicViability = 15, urgency = 10, buyerReadiness = 5, informationMarketFit = 5 });
var weightsB = MakePreferences(active: new { problemClarity = 200, hslDeliveryFit = 250, independentScope = 200, economicViability = 150, urgency = 100, buyerReadiness = 50, informationMarketFit = 50 });
var activeResultA = OpportunityRadarV2.ComposeActiveProject(activeOpportunity, weightsA, activeAssessment);
var activeResultB = OpportunityRadarV2.ComposeActiveProject(activeOpportunity, weightsB, activeAssessment);
Check(activeResultA.OpportunityScore == activeResultB.OpportunityScore && activeResultA.EffectiveWeights!.Values.Sum() == 100,
    "Active Project weights must normalize and remain scale invariant.");

var zeroWeights = MakePreferences(active: new { problemClarity = 0, hslDeliveryFit = 0, independentScope = 0, economicViability = 0, urgency = 0, buyerReadiness = 0, informationMarketFit = 0 });
Check(OpportunityRadarV2.ReadActiveWeights(zeroWeights).SequenceEqual(OpportunityRadarV2.ReadActiveWeights(weightsA)),
    "All-zero Active Project weights must fall back to versioned defaults.");

var lowConfidenceFactors = activeJudgments.ToDictionary(x => x.Key, x => x.Value with { Score = 2.9, Confidence = 0.4 });
var lowConfidenceResult = OpportunityRadarV2.ComposeActiveProject(activeOpportunity, preferences,
    activeAssessment with { Factors = lowConfidenceFactors, KindConfidence = 0.4 });
Check(lowConfidenceResult.OpportunityScore >= 75 && lowConfidenceResult.Recommendation == OpportunityRecommendation.Investigate
    && lowConfidenceResult.NeedsVerification,
    "A high score with low confidence must retain its score but require investigation.");

var blockedResult = OpportunityRadarV2.ComposeActiveProject(activeOpportunity, preferences,
    activeAssessment with { EmploymentOrStaffing = true });
Check(blockedResult.Recommendation == OpportunityRecommendation.Pass
    && blockedResult.Checks!.Any(x => x.Key == "employment" && x.Severity == EvaluationCheckSeverity.Block),
    "A deterministic Block check must force Pass.");

var businessFactors = OpportunityRadarV2.OperationalDefaults.Keys.Concat(OpportunityRadarV2.DigitalDefaults.Keys)
    .Distinct().Where(key => key != "marketAccessFit")
    .ToDictionary(key => key, _ => new JevJudgment(2.2, 0.8, "p1"));
var prospect = MakeBusinessProspect("Typed prospect", "Direct evidence supports a contained business improvement with a clear buyer and source passage.");
prospect.BusinessProspectDetail!.ImportedProspectType = BusinessProspectType.DigitalPresence;
var unknownClassification = new BusinessProspectV2Assessment(BusinessProspectType.Unknown, 0.4, businessFactors, false, false, false, "none");
Check(OpportunityRadarV2.ComposeBusinessProspect(prospect, preferences, unknownClassification).ProspectType == BusinessProspectType.DigitalPresence,
    "Imported type must be used when Jev cannot classify a prospect.");
prospect.BusinessProspectDetail.ProspectTypeOverride = BusinessProspectType.Hybrid;
var hybridResult = OpportunityRadarV2.ComposeBusinessProspect(prospect, preferences, unknownClassification);
Check(hybridResult.ProspectType == BusinessProspectType.Hybrid
    && hybridResult.RubricVersion == OpportunityRadarV2.OperationalRubricVersion,
    "Human override must take precedence, and Hybrid must use the Operational Pain profile.");

var digitalAssessment = unknownClassification with { ProspectType = BusinessProspectType.DigitalPresence, ProspectTypeConfidence = 0.9 };
prospect.BusinessProspectDetail.ProspectTypeOverride = null;
var digitalResult = OpportunityRadarV2.ComposeBusinessProspect(prospect, preferences, digitalAssessment);
Check(digitalResult.RubricVersion == OpportunityRadarV2.DigitalRubricVersion
    && digitalResult.EffectiveWeights!.Keys.SequenceEqual(OpportunityRadarV2.DigitalDefaults.Keys),
    "Digital Presence prospects must use the independent Digital Presence profile.");

// marketAccessFit is never asked to Jev - ComposeBusinessProspect always hands it a locally computed
// MarketFit() score with EvidencePassageId hardcoded to "none". A supplied industry pushes that score
// to 2, which would otherwise always trip the "unsupported factor" check regardless of evidence quality.
var marketFitProspect = MakeBusinessProspect("Market fit prospect",
    "Direct evidence supports a contained business improvement with a clear buyer and source passage.", industry: "Healthcare");
var marketFitResult = OpportunityRadarV2.ComposeBusinessProspect(marketFitProspect, preferences, unknownClassification with { ProspectType = BusinessProspectType.OperationalPain });
Check(!marketFitResult.Checks!.Any(x => x.Key == "unsupported:marketAccessFit"),
    "marketAccessFit must never trigger the unsupported-factor check, since it is never asked to Jev in the first place.");

// CSV export and injection defenses.
var dangerousExportValues = new[] { "=cmd", "+cmd", "-cmd", "@cmd", "\t=cmd", "\r\n=cmd", "＝cmd", "＋cmd", "－cmd", "＠cmd" };
Check(dangerousExportValues.All(value => OpportunityRadarReporting.SanitizeExportValue(value).StartsWith("'", StringComparison.Ordinal)),
    "CSV export should neutralize formula prefixes.");
Check(dangerousExportValues.All(value => OpportunityRadarReporting.SanitizeExportValue(value).All(character => !char.IsControl(character))),
    "CSV export should remove control characters.");
Check(OpportunityRadarReporting.CsvCell("\"breakout\",=cmd") == "\"\"\"breakout\"\",=cmd\"",
    "CSV export should quote every field and double embedded quotes.");

var exportOpportunity = MakeActiveProject("=Formula title", "Safe public description for export testing with enough content.");
exportOpportunity.Id = 4;
exportOpportunity.Notes = "@formula note";
var csv = OpportunityRadarReporting.BuildCsv([exportOpportunity], preferences);
Check(csv.StartsWith("﻿", StringComparison.Ordinal) && csv.Contains("\"'=Formula title\"") && csv.Contains("\"'@formula note\""),
    "CSV output should sanitize imported and user-written fields without changing storage.");

// Authorization.
var authorization = typeof(OpportunityRadarController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
Check(authorization.Roles == Roles.Admin, "Every Opportunity Radar endpoint should inherit the admin-only authorization boundary.");

// Jev evaluators with mocked provider responses.
var jevOpportunity = MakeActiveProject("Order workflow", "We need supplier orders connected to our accounting workflow. Budget is $8,000 and delivery is 8 weeks.");
var jevResponse = JsonSerializer.Serialize(new
{
    model = "jev-1.13.0",
    answers = new Dictionary<string, object>
    {
        ["opportunity_kind"] = ChoiceAnswer("explicit_demand"), ["project_type"] = ChoiceAnswer("integration"),
        ["problem_clarity"] = ScoreAnswer(2.8), ["problem_clarity_passage"] = ChoiceAnswer("request"),
        ["hsl_delivery_fit"] = ScoreAnswer(2.8), ["hsl_delivery_fit_passage"] = ChoiceAnswer("request"),
        ["independent_scope_value"] = ScoreAnswer(2.7), ["independent_scope_value_passage"] = ChoiceAnswer("request"),
        ["independent_scope_feasibility"] = ScoreAnswer(2.7), ["independent_scope_feasibility_passage"] = ChoiceAnswer("request"),
        ["economic_viability"] = ScoreAnswer(2.5), ["economic_viability_passage"] = ChoiceAnswer("request"),
        ["urgency"] = ScoreAnswer(2.4), ["urgency_passage"] = ChoiceAnswer("request"),
        ["buyer_readiness_access"] = ScoreAnswer(2.3), ["buyer_readiness_access_passage"] = ChoiceAnswer("request"),
        ["buyer_readiness_next_step"] = ScoreAnswer(2.3), ["buyer_readiness_next_step_passage"] = ChoiceAnswer("request"),
        ["information_market_fit_market"] = ScoreAnswer(2.5), ["information_market_fit_market_passage"] = ChoiceAnswer("request"),
        ["information_market_fit_delivery"] = ScoreAnswer(2.5), ["information_market_fit_delivery_passage"] = ChoiceAnswer("request"),
        ["employment_or_staffing"] = NoulAnswer(0.05),
        ["team_scale"] = NoulAnswer(0.05), ["core_system_replacement"] = NoulAnswer(0.05),
        ["concern_evidence"] = ChoiceAnswer("none")
    },
    usage = new { input_tokens = 321, output_tokens = 44 }
});
var handler = new SequenceHandler(
    new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } },
    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jevResponse, Encoding.UTF8, "application/json") });
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["TypeSafe:ApiKey"] = "test-key", ["TypeSafe:Model"] = "jev-latest"
}).Build();
var jev = new JevActiveProjectEvaluator(
    new HttpClient(handler) { BaseAddress = new Uri("https://api.typesafe.ai") }, configuration,
    NullLogger<JevActiveProjectEvaluator>.Instance);
var jevOutcome = await jev.EvaluateAsync(jevOpportunity, preferences, CancellationToken.None);
Check(handler.CallCount == 2, "Jev should retry a 429 within the bounded retry policy.");
Check(handler.LastRequestUri == new Uri("https://api.typesafe.ai/v1/systemone") && handler.LastAuthorizationScheme == "Bearer",
    "Jev should use the fixed TypeSafe endpoint with bearer authentication.");
Check(jevOutcome.Model == "jev-1.13.0" && jevOutcome.InputTokens == 321,
    "Jev should preserve resolved model and usage.");

var invalidHandler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(jevResponse.Replace("\"confidence\":0.8", "\"confidence\":1.8", StringComparison.Ordinal), Encoding.UTF8, "application/json")
});
try
{
    await new JevActiveProjectEvaluator(
        new HttpClient(invalidHandler) { BaseAddress = new Uri("https://api.typesafe.ai") }, configuration,
        NullLogger<JevActiveProjectEvaluator>.Instance).EvaluateAsync(jevOpportunity, preferences, CancellationToken.None);
    failures.Add("Out-of-range Jev confidence must fail explicitly.");
}
catch (OpportunityEvaluationException ex)
{
    Check(ex.Code == "invalid_response", "Malformed Jev values must use the invalid_response code.");
}

var prospectOpportunity = MakeBusinessProspect(
    "Example Roofing Co", "Established roofing company with strong reviews. The website is outdated and has no online booking.",
    "HomeServices", "Local");
prospectOpportunity.ResearchConfidence = ResearchConfidence.High;
prospectOpportunity.ResearchConfidenceReason = "Sourcing agent reason that must stay out of the provider request.";
prospectOpportunity.ResearchAgent = "private-agent-name";
prospectOpportunity.BusinessProspectDetail!.ImportedProspectType = BusinessProspectType.OperationalPain;
var prospectResponse = JsonSerializer.Serialize(new
{
    model = "jev-1.13.0",
    answers = new Dictionary<string, object>
    {
        ["prospect_type"] = ChoiceAnswer("digital_presence"),
        ["pain_cost_severity"] = ScoreAnswer(0.8), ["pain_cost_severity_passage"] = ChoiceAnswer("fact-1"),
        ["pain_frequency"] = ScoreAnswer(0.8), ["pain_frequency_passage"] = ChoiceAnswer("fact-1"),
        ["automation_feasibility"] = ScoreAnswer(1.1), ["automation_feasibility_passage"] = ChoiceAnswer("fact-1"),
        ["economic_leverage"] = ScoreAnswer(1.3), ["economic_leverage_passage"] = ChoiceAnswer("fact-1"),
        ["contained_engagement"] = ScoreAnswer(2.4), ["contained_engagement_passage"] = ChoiceAnswer("fact-1"),
        ["urgency"] = ScoreAnswer(2.1), ["urgency_passage"] = ChoiceAnswer("fact-1"),
        ["hsl_delivery_fit"] = ScoreAnswer(2.6), ["hsl_delivery_fit_passage"] = ChoiceAnswer("fact-1"),
        ["buyer_access"] = ScoreAnswer(2.1), ["buyer_access_passage"] = ChoiceAnswer("fact-1"),
        ["business_strength"] = ScoreAnswer(2.6), ["business_strength_passage"] = ChoiceAnswer("fact-1"),
        ["digital_weakness"] = ScoreAnswer(2.8), ["digital_weakness_passage"] = ChoiceAnswer("fact-1"),
        ["reputation_mismatch"] = ScoreAnswer(2.4), ["reputation_mismatch_passage"] = ChoiceAnswer("fact-1"),
        ["entry_project_strength"] = ScoreAnswer(2.5), ["entry_project_strength_passage"] = ChoiceAnswer("fact-1"),
        ["speculative_workflow"] = NoulAnswer(0.1), ["physical_or_judgment_heavy"] = NoulAnswer(0.1),
        ["core_system_replacement"] = NoulAnswer(0.1),
        ["concern_evidence"] = ChoiceAnswer("none")
    },
    usage = new { input_tokens = 210, output_tokens = 38 }
});
var prospectHandler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(prospectResponse, Encoding.UTF8, "application/json")
});
var prospectJev = new JevBusinessProspectEvaluator(
    new HttpClient(prospectHandler) { BaseAddress = new Uri("https://api.typesafe.ai") }, configuration,
    NullLogger<JevBusinessProspectEvaluator>.Instance);
var prospectOutcome = await prospectJev.EvaluateAsync(prospectOpportunity, preferences, CancellationToken.None);
Check(prospectOutcome.Model == "jev-1.13.0" && prospectOutcome.InputTokens == 210,
    "Business Prospect Jev evaluation should preserve model and usage.");
Check(prospectHandler.LastRequestBody is not null
      && !prospectHandler.LastRequestBody.Contains("private-agent-name", StringComparison.Ordinal)
      && !prospectHandler.LastRequestBody.Contains("Sourcing agent reason", StringComparison.Ordinal)
      && !prospectHandler.LastRequestBody.Contains("OperationalPain", StringComparison.Ordinal),
    "The Jev request must be blind to imported agent identity, confidence, and classification.");
var differentPreferences = MakePreferences(excludedIndustries: ["SomethingElseEntirely"]);
Check(prospectJev.EstimateMaximumInputTokens(prospectOpportunity, preferences)
      == prospectJev.EstimateMaximumInputTokens(prospectOpportunity, differentPreferences),
    "Business Prospect Jev requests must not depend on local preferences.");

// pain_evidence was split into pain_cost_severity/pain_frequency and must be combined back into one
// painEvidence factor: the score averages the two sub-questions, and the cited evidence comes from
// whichever sub-question scored higher (here, cost severity), not necessarily the first one.
var combineResponse = JsonSerializer.Serialize(new
{
    model = "jev-1.13.0",
    answers = new Dictionary<string, object>
    {
        ["prospect_type"] = ChoiceAnswer("digital_presence"),
        ["pain_cost_severity"] = ScoreAnswer(3.0), ["pain_cost_severity_passage"] = ChoiceAnswer("fact-1"),
        ["pain_frequency"] = ScoreAnswer(0.0), ["pain_frequency_passage"] = ChoiceAnswer("none"),
        ["automation_feasibility"] = ScoreAnswer(1.1), ["automation_feasibility_passage"] = ChoiceAnswer("fact-1"),
        ["economic_leverage"] = ScoreAnswer(1.3), ["economic_leverage_passage"] = ChoiceAnswer("fact-1"),
        ["contained_engagement"] = ScoreAnswer(2.4), ["contained_engagement_passage"] = ChoiceAnswer("fact-1"),
        ["urgency"] = ScoreAnswer(2.1), ["urgency_passage"] = ChoiceAnswer("fact-1"),
        ["hsl_delivery_fit"] = ScoreAnswer(2.6), ["hsl_delivery_fit_passage"] = ChoiceAnswer("fact-1"),
        ["buyer_access"] = ScoreAnswer(2.1), ["buyer_access_passage"] = ChoiceAnswer("fact-1"),
        ["business_strength"] = ScoreAnswer(2.6), ["business_strength_passage"] = ChoiceAnswer("fact-1"),
        ["digital_weakness"] = ScoreAnswer(2.8), ["digital_weakness_passage"] = ChoiceAnswer("fact-1"),
        ["reputation_mismatch"] = ScoreAnswer(2.4), ["reputation_mismatch_passage"] = ChoiceAnswer("fact-1"),
        ["entry_project_strength"] = ScoreAnswer(2.5), ["entry_project_strength_passage"] = ChoiceAnswer("fact-1"),
        ["speculative_workflow"] = NoulAnswer(0.1), ["physical_or_judgment_heavy"] = NoulAnswer(0.1),
        ["core_system_replacement"] = NoulAnswer(0.1),
        ["concern_evidence"] = ChoiceAnswer("none")
    },
    usage = new { input_tokens = 210, output_tokens = 38 }
});
var combineHandler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent(combineResponse, Encoding.UTF8, "application/json")
});
var combineJev = new JevBusinessProspectEvaluator(
    new HttpClient(combineHandler) { BaseAddress = new Uri("https://api.typesafe.ai") }, configuration,
    NullLogger<JevBusinessProspectEvaluator>.Instance);
var combineOutcome = await combineJev.EvaluateAsync(prospectOpportunity, preferences, CancellationToken.None);
var combineAssessment = OpportunityRadarV2.DeserializeBusiness(combineOutcome.AssessmentJson);
Check(combineAssessment is not null && Math.Abs(combineAssessment.Factors["painEvidence"].Score - 1.5) < 0.001
      && combineAssessment.Factors["painEvidence"].EvidencePassageId == "fact-1",
    "Combined pain_evidence must average the split cost/frequency scores and cite whichever sub-question scored higher.");

// Prospect type is now a purely explicit field (no more regex fallback scanning evidence text).
Check(OpportunityImportService.ParseProspectType("OperationalPain") == BusinessProspectType.OperationalPain,
    "An explicit prospect type value must still parse.");
Check(OpportunityImportService.ParseProspectType(null) is null, "An omitted prospect type must resolve to null, not throw.");

// Regression: EvaluationCheckSeverity (and the other enums embedded in ResultJson) used to serialize as a
// raw number, so the frontend's `check.severity === 'Review'` comparison against the TypeScript string
// union was always false and every check silently rendered as "Info". CamelCaseOptions/CaseInsensitiveOptions
// now carry a JsonStringEnumConverter; check both the new write shape and that rows already stored under
// the old numeric format still deserialize correctly (the converter's reader accepts either).
var severityJson = JsonSerializer.Serialize(
    new EvaluationCheck("physicalOrJudgmentHeavy", EvaluationCheckSeverity.Review, "explanation", "none"),
    OpportunityRadarEngine.CamelCaseOptions);
Check(severityJson.Contains("\"severity\":\"Review\"", StringComparison.Ordinal),
    "EvaluationCheckSeverity must serialize as its string name, not a raw number, or the frontend's severity === 'Review' comparisons silently always fail.");
var legacyNumericCheck = JsonSerializer.Deserialize<EvaluationCheck>(
    """{"key":"physicalOrJudgmentHeavy","severity":1,"explanation":"explanation","evidencePassageId":"none"}""",
    OpportunityRadarEngine.CaseInsensitiveOptions);
Check(legacyNumericCheck?.Severity == EvaluationCheckSeverity.Review,
    "Rows evaluated before the string-enum fix stored severity as a raw number; those must keep deserializing correctly.");

if (string.Equals(Environment.GetEnvironmentVariable("RADAR_RUN_LIVE_CONTRACT"), "true", StringComparison.OrdinalIgnoreCase))
{
    var liveKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") ?? Environment.GetEnvironmentVariable("TypeSafe__ApiKey");
    if (string.IsNullOrWhiteSpace(liveKey)) failures.Add("RADAR_RUN_LIVE_CONTRACT requires a TypeSafe API key.");
    else
    {
        var liveConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TypeSafe:ApiKey"] = liveKey, ["TypeSafe:Model"] = "jev-latest"
        }).Build();
        using var liveClient = new HttpClient { BaseAddress = new Uri("https://api.typesafe.ai"), Timeout = TimeSpan.FromSeconds(15) };
        var liveActive = await new JevActiveProjectEvaluator(liveClient, liveConfiguration, NullLogger<JevActiveProjectEvaluator>.Instance)
            .EvaluateAsync(jevOpportunity, preferences, CancellationToken.None);
        var liveProspect = await new JevBusinessProspectEvaluator(liveClient, liveConfiguration, NullLogger<JevBusinessProspectEvaluator>.Instance)
            .EvaluateAsync(prospectOpportunity, preferences, CancellationToken.None);
        Check(liveActive.Model.StartsWith("jev-", StringComparison.Ordinal) && liveProspect.Model.StartsWith("jev-", StringComparison.Ordinal),
            "Both opt-in live contracts should return resolved Jev models.");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Opportunity Radar unit checks failed ({failures.Count}):");
    foreach (var failure in failures) Console.Error.WriteLine($"  - {failure}");
    return 1;
}

Console.WriteLine("Opportunity Radar unit checks passed.");
return 0;

static object ChoiceAnswer(string choice) => new
{
    type = "choice", choice,
    probabilities = new Dictionary<string, double> { [choice] = 1 }, confidence = 1
};
static object NoulAnswer(double value) => new { type = "noul", noul = value };
static object ScoreAnswer(double value) => new
{
    type = "score", score = value, legend = new Dictionary<string, string>(),
    probabilities = new Dictionary<string, double>(), confidence = 0.8
};

sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = new(responses);
    public int CallCount { get; private set; }
    public Uri? LastRequestUri { get; private set; }
    public string? LastAuthorizationScheme { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri;
        LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return responses.Dequeue();
    }
}
