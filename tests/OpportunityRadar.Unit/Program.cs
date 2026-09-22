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

RadarPreferences MakePreferences(decimal minimumBudget = 2500, int minimumWeeks = 2, int maximumWeeks = 12,
    string[]? capabilities = null, object? activeWeights = null, string[]? excludedIndustries = null, object? prospectWeights = null) => new()
{
    ActiveProjectPreferencesJson = JsonSerializer.Serialize(new
    {
        capabilities = capabilities ?? new[] { ".NET", "React", "SQL", "REST APIs" },
        preferredProjectTypes = Array.Empty<string>(), excludedProjectTypes = Array.Empty<string>(),
        minimumBudget, minimumWeeks, maximumWeeks, incompleteInformationTolerance = "Medium",
        weights = activeWeights ?? new { }
    }),
    BusinessProspectPreferencesJson = JsonSerializer.Serialize(new
    {
        preferredIndustries = Array.Empty<string>(), excludedIndustries = excludedIndustries ?? Array.Empty<string>(),
        preferredGeographies = Array.Empty<string>(), excludedGeographies = Array.Empty<string>(),
        weights = prospectWeights ?? new { }
    }),
    DigestActiveProjectCount = 3, DigestBusinessProspectCount = 2
};

Opportunity MakeActiveProject(string title, string description, ActiveProjectSourceType type = ActiveProjectSourceType.ExplicitDemand) => new()
{
    EntityType = OpportunityEntityType.ActiveProject, Title = title, Description = description,
    SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.Segment(description)),
    ActiveProjectDetail = new ActiveProjectDetail { DeclaredSourceType = type }
};

Opportunity MakeBusinessProspect(string title, string description, string? industry = null, string? geography = null) => new()
{
    EntityType = OpportunityEntityType.BusinessProspect, Title = title, Description = description,
    SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.Segment(description)),
    BusinessProspectDetail = new BusinessProspectDetail
    {
        NormalizedBusinessName = OpportunityRadarEngine.NormalizeBusinessName(title), Industry = industry, Geography = geography
    }
};

var preferences = MakePreferences();

// --- Active project ------------------------------------------------------------------------

var strong = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Reconciliation",
    "We need a tool to reconcile Shopify orders with supplier spreadsheets and report exceptions. Budget is $8,000 and delivery is 8 weeks."), preferences).Result;
Check(strong.Recommendation == OpportunityRecommendation.Pursue, "Strong explicit integration demand should be Pursue.");

var semantic = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Remove order entry",
    "Our coordinator copies orders from attachments into vendor systems every morning. We need the repeated entry automated. Budget is $6,000."), preferences).Result;
Check(semantic.Factors.Single(x => x.Key == "fit").Score == 3, "Automation without stack keywords should still have strong semantic fit.");

var employment = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("React .NET role",
    "Full-time employee role leading a multi-year React .NET SQL platform team with salary and benefits."), preferences).Result;
Check(employment.Recommendation == OpportunityRecommendation.Pass && employment.Kind == ActiveProjectKind.FullTimeRole.ToString(), "Full-time keyword match should be Pass.");

var missingBudget = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Portal",
    "We need a secure customer portal that provides monthly reports and downloads within 10 weeks."), preferences).Result;
Check(missingBudget.BudgetStatus == BudgetStatus.Unknown && missingBudget.Recommendation == OpportunityRecommendation.Investigate, "Missing budget should remain unknown and require investigation.");

var lowBudget = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Dashboard",
    "We need a React dashboard connected to an API. Fixed budget is $500 and delivery is two weeks."), preferences).Result;
Check(lowBudget.BudgetStatus == BudgetStatus.Incompatible && lowBudget.Recommendation == OpportunityRecommendation.Pass, "Budget below preference should be Pass.");

var rescoredPreferences = MakePreferences(minimumBudget: 400);
var rescoredBudget = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Dashboard",
    "We need a React dashboard connected to an API. Fixed budget is $500 and delivery is 2 weeks."), rescoredPreferences).Result;
Check(rescoredBudget.BudgetStatus == BudgetStatus.Compatible, "Budget compatibility must use persisted preferences rather than a hardcoded floor.");

var longProject = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Integration",
    "We need an order integration with exception reporting. Budget is $20,000 and the delivery timeline is 20 weeks."), preferences).Result;
Check(longProject.Recommendation == OpportunityRecommendation.Investigate && longProject.Concerns.Any(x => x.Contains("12-week")), "Scope recommendation must use the configured maximum window.");

var weightedPreferences = MakePreferences(activeWeights: new { capabilityFit = 0, problemClarity = 0, independentScope = 0, informationSufficiency = 1 });
var weighted = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Sparse integration",
    "We need an integration. Budget is $8,000 and delivery is 8 weeks."), weightedPreferences).Result;
Check(weighted.PriorityBand == PriorityBand.Medium,
    "Priority scoring must read editable weights from persisted preferences: putting all weight on a factor scored 2/3 " +
    "should land Medium once weights are normalized, not be crushed to Low by a small raw weight sum.");

var signal = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Manual work",
    "An employee rekeys orders into spreadsheets every day.", ActiveProjectSourceType.OperationalSignal), preferences).Result;
Check(signal.Kind == ActiveProjectKind.OperationalSignal.ToString() && signal.Recommendation != OpportunityRecommendation.Pursue, "Operational signal must not become explicit buying demand.");

var vague = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Need an app", "We need an app to make our business better. Please send a quote."), preferences).Result;
Check(vague.Recommendation == OpportunityRecommendation.Investigate && vague.MissingInformation.Count > 0,
    "A vague request should require clarification rather than becoming confident demand.");

var apiRisk = OpportunityRadarEngine.EvaluateActiveProject(MakeActiveProject("Vendor synchronization",
    "Build an integration with our legacy vendor. API access is pending vendor approval and the undocumented API may not support updates. Budget is $9,000."), preferences).Result;
Check(apiRisk.Recommendation == OpportunityRecommendation.Investigate && apiRisk.Concerns.Any(x => x.Contains("API access", StringComparison.OrdinalIgnoreCase)),
    "An uncertain third-party API dependency should require investigation.");

var keywordMissOpportunity = MakeActiveProject("Remove daily order rekeying",
    "Our operations coordinator copies new orders from email attachments into three vendor systems every morning. We want the repeated entry removed and failures surfaced for review. Budget is $6,000.");
var keywordMiss = OpportunityRadarReporting.KeywordBaseline(keywordMissOpportunity, preferences);
Check(keywordMiss.KeywordScore == 0 && OpportunityRadarEngine.EvaluateActiveProject(keywordMissOpportunity, preferences).Result.Factors.Single(x => x.Key == "fit").Score == 3,
    "The inspectable baseline should expose a useful semantic fit that literal terms miss.");
var keywordFalsePositive = OpportunityRadarReporting.KeywordBaseline(
    MakeActiveProject("React .NET SQL AWS role", "Full-time principal engineer for React, .NET, SQL, and AWS with salary and benefits."), preferences);
Check(keywordFalsePositive.KeywordScore == 3 && keywordFalsePositive.Recommendation == OpportunityRecommendation.Pass
    && keywordFalsePositive.HardRules.Any(x => x.Key == "full_time_role" && x.Triggered),
    "A strong keyword match should remain inspectably blocked by the full-time hard rule.");

const string sourceText = "First  source paragraph.\nWith an internal line.\n\nSecond source paragraph.";
var passages = OpportunityRadarEngine.Segment(sourceText);
Check(passages.Count == 2 && passages[0].Text == "First  source paragraph.\nWith an internal line." && sourceText.Contains(passages[0].Text, StringComparison.Ordinal), "Passages must preserve verbatim source text in stable order.");
Check(OpportunityRadarEngine.Fingerprint("A", "B", null) == OpportunityRadarEngine.Fingerprint("a", "b", null), "Fingerprint normalization should be case-insensitive.");
Check(OpportunityRadarEngine.Similarity("shopify orders supplier spreadsheet", "shopify order supplier spreadsheets") > 0.3, "Near-duplicate similarity should recognize overlapping records.");
Check(OpportunityRadarEngine.Similarity(
    "Shopify supplier reconciliation We need a tool that reconciles Shopify orders with supplier spreadsheets flags mismatches and produces a daily exception report Budget is 8000 and delivery expected within 8 weeks",
    "Shopify order and supplier reconciliation We need a tool to reconcile Shopify orders against supplier spreadsheets highlight mismatches and send a daily exception report Budget is 8000 with an 8 week delivery target") >= 0.62,
    "The synthetic Active Project near-duplicate pair should cross the review threshold.");

// --- Business prospect ----------------------------------------------------------------------

Check(OpportunityRadarEngine.NormalizeWebsiteDomain("https://www.Example-Site.test/path") == "example-site.test", "Website domain normalization should lowercase and strip www.");
Check(OpportunityRadarEngine.NormalizeWebsiteDomain(null) == "" && OpportunityRadarEngine.NormalizeWebsiteDomain("not a url") == "", "Website domain normalization should fail safe on missing or invalid URLs.");
Check(OpportunityRadarEngine.Similarity(OpportunityRadarEngine.NormalizeBusinessName("Harbor View Physical Therapy"),
    OpportunityRadarEngine.NormalizeBusinessName("Harbor View Physical Therapy Clinic")) >= 0.72,
    "The synthetic Business Prospect near-duplicate pair should cross the review threshold.");

var strongProspect = OpportunityRadarEngine.EvaluateBusinessProspect(MakeBusinessProspect("Riverside Family Dental",
    "Established dental practice, well known locally, 5-star reviews and loyal customers for over fifteen years. The website is outdated, not mobile friendly, and has no online booking system. The practice needs a new website with online booking.",
    "Healthcare", "Local"), preferences).Result;
Check(strongProspect.Recommendation == OpportunityRecommendation.Prioritize, "A strong, established business with a clear digital-presence gap should be Prioritize.");
Check(strongProspect.Factors.Single(x => x.Key == "reputationMismatch").Score >= 2, "Strong reputation combined with a weak website should score a real reputation mismatch.");
Check(!strongProspect.KnownFacts.Any(x => x.StartsWith("Buying intent evidence detected", StringComparison.Ordinal))
    && strongProspect.MissingInformation.Any(x => x.Contains("expected for a cold prospect", StringComparison.OrdinalIgnoreCase))
    && !strongProspect.Concerns.Any(x => x.Contains("buying intent", StringComparison.OrdinalIgnoreCase)),
    "Unknown buying intent must be listed as missing information, never as a concern, and must not be inferred from a weak website.");

var alreadyModern = OpportunityRadarEngine.EvaluateBusinessProspect(MakeBusinessProspect("Crestline Auto Body",
    "Well-regarded auto body shop with a modern website, mobile friendly, recently redesigned, with online booking already in place.",
    "Automotive", "Local"), preferences).Result;
Check(alreadyModern.Recommendation == OpportunityRecommendation.Skip && alreadyModern.Factors.Single(x => x.Key == "digitalWeakness").Score == 0,
    "A business that already has a strong digital presence should not be prioritized for outreach.");

var explicitIntent = OpportunityRadarEngine.EvaluateBusinessProspect(MakeBusinessProspect("Downtown Fitness Studio",
    "The studio owner posted a job for a developer to rebuild their outdated booking website and has been requesting quotes from local agencies. Established, well known, and trusted in the community for a decade.",
    "Fitness", "Local"), preferences).Result;
Check(explicitIntent.KnownFacts.Any(x => x.Contains("Buying intent evidence detected", StringComparison.Ordinal)),
    "Explicit buying-intent language should be surfaced as a known fact once it is actually present.");

var excludedIndustryPreferences = MakePreferences(excludedIndustries: ["Legal"]);
var excludedIndustry = OpportunityRadarEngine.EvaluateBusinessProspect(MakeBusinessProspect("Bayview Legal Group",
    "An established law firm with an outdated website and no online intake form.", "Legal", "Regional"), excludedIndustryPreferences).Result;
Check(excludedIndustry.Recommendation == OpportunityRecommendation.Skip && excludedIndustry.Concerns.Any(x => x.Contains("excluded", StringComparison.OrdinalIgnoreCase)),
    "An excluded industry must be a hard skip regardless of how strong the other factors are.");

var noContact = OpportunityRadarEngine.EvaluateBusinessProspect(MakeBusinessProspect("Northgate Landscaping",
    "An established landscaping company with an outdated site. No phone number listed and no way to reach the business was found anywhere online.",
    "HomeServices", "Local"), preferences).Result;
Check(noContact.Recommendation == OpportunityRecommendation.Skip && noContact.Factors.Single(x => x.Key == "contactability").Score == 0
    && noContact.Concerns.Any(x => x.Contains("contact method", StringComparison.OrdinalIgnoreCase)),
    "A business with no discoverable contact method must be a hard skip and must say why.");

var thinEvidence = OpportunityRadarEngine.EvaluateBusinessProspect(MakeBusinessProspect("Downtown Coffee Cart", "A small coffee cart."), preferences).Result;
Check(thinEvidence.Factors.Single(x => x.Key == "evidenceCompleteness").Score == 0 && thinEvidence.Recommendation == OpportunityRecommendation.Skip,
    "Thin research notes should not be enough to justify outreach time.");

var comparisonUnavailable = OpportunityRadarReporting.CsvCell(null);
Check(comparisonUnavailable == "\"\"", "The reporting sanitizer should handle a null baseline value safely for Business Prospect rows.");

// --- Weight normalization -----------------------------------------------------------------

var scaledOpportunity = MakeActiveProject("Reconciliation scale check",
    "We need a tool to reconcile Shopify orders with supplier spreadsheets and report exceptions. Budget is $8,000 and delivery is 8 weeks.");
var proportionalWeightsA = MakePreferences(activeWeights: new { capabilityFit = 35, problemClarity = 30, independentScope = 20, informationSufficiency = 15 });
var proportionalWeightsB = MakePreferences(activeWeights: new { capabilityFit = 350, problemClarity = 300, independentScope = 200, informationSufficiency = 150 });
var proportionalResultA = OpportunityRadarEngine.EvaluateActiveProject(scaledOpportunity, proportionalWeightsA).Result;
var proportionalResultB = OpportunityRadarEngine.EvaluateActiveProject(scaledOpportunity, proportionalWeightsB).Result;
Check(proportionalResultA.PriorityBand == proportionalResultB.PriorityBand && proportionalResultA.Recommendation == proportionalResultB.Recommendation,
    "Weights that are proportionally identical but differently scaled must normalize to the same ranking, not drift with raw magnitude.");

var allOnesWeights = MakePreferences(activeWeights: new { capabilityFit = 1, problemClarity = 1, independentScope = 1, informationSufficiency = 1 });
var allOnesResult = OpportunityRadarEngine.EvaluateActiveProject(scaledOpportunity, allOnesWeights).Result;
Check(allOnesResult.PriorityBand != PriorityBand.Low,
    "Setting every Active Project weight to 1 must not silently collapse a strong opportunity's ranking to Low.");

var zeroActiveWeightsRead = OpportunityRadarEngine.ReadActiveProjectPreferences(
    MakePreferences(activeWeights: new { capabilityFit = 0, problemClarity = 0, independentScope = 0, informationSufficiency = 0 }));
Check(zeroActiveWeightsRead.Weights is { CapabilityFit: 0, ProblemClarity: 0, IndependentScope: 0, InformationSufficiency: 0 },
    "An all-zero Active Project weight submission must be read back safely rather than dividing by zero.");

var prospectAllOnesWeights = MakePreferences(prospectWeights: new
{
    businessStrength = 1, digitalPresenceWeakness = 1, reputationMismatch = 1, entryProjectStrength = 1, geography = 1, contactability = 1, evidenceCompleteness = 1
});
var prospectAllOnesResult = OpportunityRadarEngine.EvaluateBusinessProspect(MakeBusinessProspect("Riverside Family Dental",
    "Established dental practice, well known locally, 5-star reviews and loyal customers for over fifteen years. The website is outdated, not mobile friendly, and has no online booking system. The practice needs a new website with online booking.",
    "Healthcare", "Local"), prospectAllOnesWeights).Result;
Check(prospectAllOnesResult.PriorityBand != PriorityBand.Low,
    "Setting every Business Prospect weight to 1 must not silently collapse a strong prospect's ranking to Low.");

var zeroProspectWeightsRead = OpportunityRadarEngine.ReadBusinessProspectPreferences(MakePreferences(prospectWeights: new
{
    businessStrength = 0, digitalPresenceWeakness = 0, reputationMismatch = 0, entryProjectStrength = 0, geography = 0, contactability = 0, evidenceCompleteness = 0
}));
Check(zeroProspectWeightsRead.Weights is { BusinessStrength: 0, DigitalPresenceWeakness: 0, ReputationMismatch: 0, EntryProjectStrength: 0, Geography: 0, Contactability: 0, EvidenceCompleteness: 0 },
    "An all-zero Business Prospect weight submission must be read back safely rather than dividing by zero.");

// --- Preferences equality (resimulation change-detection) ----------------------------------

var preferencesX = new ActiveProjectPreferences(["A", "B"], [], [], 2500m, 2, 12, IncompleteInformationTolerance.Medium, new ActiveProjectWeights(35, 30, 20, 15));
var preferencesXReordered = new ActiveProjectPreferences(["B", "A"], [], [], 2500m, 2, 12, IncompleteInformationTolerance.Medium, new ActiveProjectWeights(35, 30, 20, 15));
Check(OpportunityRadarEngine.ActiveProjectPreferencesEqual(preferencesX, preferencesXReordered),
    "Record equality on ActiveProjectPreferences falls back to array reference equality, so the comparer used to gate resimulation must compare content, not instances.");
Check(!OpportunityRadarEngine.ActiveProjectPreferencesEqual(preferencesX, preferencesX with { MinimumBudget = 3000m }),
    "The Active Project preferences comparer must still detect a genuine change.");

var prospectPreferencesX = new BusinessProspectPreferences(["Healthcare"], [], [], [], new BusinessProspectWeights(15, 25, 15, 20, 5, 10, 10));
var prospectPreferencesXReordered = new BusinessProspectPreferences(["Healthcare"], [], [], [], new BusinessProspectWeights(15, 25, 15, 20, 5, 10, 10));
Check(OpportunityRadarEngine.BusinessProspectPreferencesEqual(prospectPreferencesX, prospectPreferencesXReordered),
    "Two structurally identical BusinessProspectPreferences built from separate array instances must compare equal.");
Check(!OpportunityRadarEngine.BusinessProspectPreferencesEqual(prospectPreferencesX, prospectPreferencesX with { PreferredIndustries = ["Legal"] }),
    "The Business Prospect preferences comparer must still detect a genuine change.");

// --- CSV export and injection defenses -------------------------------------------------------

var dangerousExportValues = new[] { "=cmd", "+cmd", "-cmd", "@cmd", "\t=cmd", "\r\n=cmd", "＝cmd", "＋cmd", "－cmd", "＠cmd" };
Check(dangerousExportValues.All(value => OpportunityRadarReporting.SanitizeExportValue(value).StartsWith("'", StringComparison.Ordinal)),
    "CSV export should neutralize ASCII and full-width formula prefixes, including after control characters.");
Check(dangerousExportValues.All(value => OpportunityRadarReporting.SanitizeExportValue(value).All(character => !char.IsControl(character))),
    "CSV export should remove tabs, line breaks, and other control characters from exported cells.");
Check(OpportunityRadarReporting.CsvCell("\"breakout\",=cmd") == "\"\"\"breakout\"\",=cmd\"",
    "CSV export should quote every field and double embedded quotes.");

var exportOpportunity = MakeActiveProject("=Formula title", "Safe public description for export testing with enough content.");
exportOpportunity.Id = 4; exportOpportunity.Notes = "@formula note";
var csv = OpportunityRadarReporting.BuildCsv([exportOpportunity], preferences);
Check(csv.StartsWith("﻿", StringComparison.Ordinal) && csv.Contains("\"'=Formula title\"") && csv.Contains("\"'@formula note\"") && csv.Contains("\"ActiveProject\""),
    "CSV output should be UTF-8 friendly, carry the entity_type column, and sanitize imported and user-written fields without changing storage.");

var prospectExportOpportunity = MakeBusinessProspect("Clean Prospect Co", "A safe public research note with enough content to pass validation.", "Retail", "Local");
prospectExportOpportunity.Id = 5;
prospectExportOpportunity.BusinessProspectDetail!.WebsiteUrl = "https://example-clean-prospect.test";
prospectExportOpportunity.BusinessProspectDetail!.NormalizedWebsiteDomain = "example-clean-prospect.test";
var prospectCsv = OpportunityRadarReporting.BuildCsv([prospectExportOpportunity], preferences);
Check(prospectCsv.Contains("\"BusinessProspect\"") && prospectCsv.Contains("\"example-clean-prospect.test\""),
    "CSV export should include Business Prospect rows with their own entity type and website domain, and no keyword baseline computation should be attempted for them.");

// --- Authorization -----------------------------------------------------------------------

var authorization = typeof(OpportunityRadarController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
Check(authorization.Roles == Roles.Admin, "Every Opportunity Radar endpoint should inherit the controller's admin-only authorization boundary.");

// --- Jev evaluators (mocked provider) -----------------------------------------------------

var jevOpportunity = MakeActiveProject("Order workflow", "We need supplier orders connected to our accounting workflow. Budget is $8,000 and delivery is 8 weeks.");
var jevResponse = JsonSerializer.Serialize(new
{
    model = "jev-1.13.0",
    answers = new Dictionary<string, object>
    {
        ["opportunity_kind"] = ChoiceAnswer("explicit_demand"),
        ["project_type"] = ChoiceAnswer("integration"),
        ["problem_concreteness"] = NoulAnswer(0.92),
        ["capability_fit"] = ScoreAnswer(2.8),
        ["solo_feasibility"] = NoulAnswer(0.9),
        ["information_sufficiency"] = ScoreAnswer(2.2),
        ["dependency_risk"] = ChoiceAnswer("none"),
        ["problem_evidence"] = ChoiceAnswer("p1"),
        ["fit_evidence"] = ChoiceAnswer("p1"),
        ["concern_evidence"] = ChoiceAnswer("none")
    },
    usage = new { input_tokens = 321, output_tokens = 44 }
});
var handler = new SequenceHandler(
    new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } },
    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jevResponse, Encoding.UTF8, "application/json") });
var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.typesafe.ai") };
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["TypeSafe:ApiKey"] = "test-key", ["TypeSafe:Model"] = "jev-latest"
}).Build();
var jev = new JevActiveProjectEvaluator(client, configuration, NullLogger<JevActiveProjectEvaluator>.Instance);
var jevOutcome = await jev.EvaluateAsync(jevOpportunity, preferences, CancellationToken.None);
Check(handler.CallCount == 2, "Jev should retry a 429 response at most within the bounded retry policy.");
Check(handler.LastRequestUri == new Uri("https://api.typesafe.ai/v1/systemone") && handler.LastAuthorizationScheme == "Bearer",
    "Jev Active Project evaluator should use the fixed TypeSafe endpoint with bearer authentication.");
Check(jevOutcome.Model == "jev-1.13.0" && jevOutcome.InputTokens == 321, "Jev should preserve resolved model and reported usage.");
Check(jevOutcome.Result.Recommendation == OpportunityRecommendation.Pursue && jevOutcome.Result.Factors.All(x => x.EvidencePassageId is "p1" or "none"),
    "Jev judgments should be composed with local recommendation rules and validated passage references.");
Check(jev.EstimateMaximumInputTokens(jevOpportunity, preferences) > jevOpportunity.Description.Length,
    "The conservative Jev estimate should include the exact serialized request, not only description length.");

var prospectOpportunity = MakeBusinessProspect("Example Roofing Co",
    "Established roofing company with strong reviews. The website is outdated and has no online booking.", "HomeServices", "Local");
var prospectResponse = JsonSerializer.Serialize(new
{
    model = "jev-1.13.0",
    answers = new Dictionary<string, object>
    {
        ["buying_intent"] = ChoiceAnswer("unknown"),
        ["business_strength"] = ScoreAnswer(2.6),
        ["digital_presence_weakness"] = ScoreAnswer(2.8),
        ["reputation_website_mismatch"] = ScoreAnswer(2.4),
        ["entry_project_strength"] = ScoreAnswer(2.5),
        ["contactability"] = ScoreAnswer(2.1),
        ["evidence_completeness"] = ScoreAnswer(2.2),
        ["entry_project_evidence"] = ChoiceAnswer("p1"),
        ["reputation_evidence"] = ChoiceAnswer("p1"),
        ["contact_evidence"] = ChoiceAnswer("none")
    },
    usage = new { input_tokens = 210, output_tokens = 38 }
});
var prospectHandler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(prospectResponse, Encoding.UTF8, "application/json") });
var prospectClient = new HttpClient(prospectHandler) { BaseAddress = new Uri("https://api.typesafe.ai") };
var prospectJev = new JevBusinessProspectEvaluator(prospectClient, configuration, NullLogger<JevBusinessProspectEvaluator>.Instance);
var prospectOutcome = await prospectJev.EvaluateAsync(prospectOpportunity, preferences, CancellationToken.None);
Check(prospectHandler.LastRequestUri == new Uri("https://api.typesafe.ai/v1/systemone") && prospectHandler.LastAuthorizationScheme == "Bearer",
    "Jev Business Prospect evaluator should use the fixed TypeSafe endpoint with bearer authentication.");
Check(prospectOutcome.Model == "jev-1.13.0" && prospectOutcome.InputTokens == 210, "Jev Business Prospect evaluator should preserve resolved model and reported usage.");
Check(prospectOutcome.Result.Recommendation is OpportunityRecommendation.Prioritize or OpportunityRecommendation.Watch,
    "Jev Business Prospect judgments should compose into a valid Business Prospect recommendation.");
var prospectEstimateBefore = prospectJev.EstimateMaximumInputTokens(prospectOpportunity, preferences);
var differentProspectPreferences = MakePreferences(excludedIndustries: ["SomethingElseEntirely"], prospectWeights: new { businessStrength = 99 });
var prospectEstimateAfter = prospectJev.EstimateMaximumInputTokens(prospectOpportunity, differentProspectPreferences);
Check(prospectEstimateBefore == prospectEstimateAfter,
    "The Business Prospect Jev request must not depend on preferences (its byte size cannot change when only preferences change), so no preference change can ever make a live result stale.");

if (string.Equals(Environment.GetEnvironmentVariable("RADAR_RUN_LIVE_CONTRACT"), "true", StringComparison.OrdinalIgnoreCase))
{
    var liveKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") ?? Environment.GetEnvironmentVariable("TypeSafe__ApiKey");
    if (string.IsNullOrWhiteSpace(liveKey))
    {
        failures.Add("RADAR_RUN_LIVE_CONTRACT requires TYPESAFE_API_KEY or TypeSafe__ApiKey.");
    }
    else
    {
        var liveConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TypeSafe:ApiKey"] = liveKey, ["TypeSafe:Model"] = "jev-latest"
        }).Build();
        using var liveClient = new HttpClient { BaseAddress = new Uri("https://api.typesafe.ai"), Timeout = TimeSpan.FromSeconds(15) };
        var liveEvaluator = new JevActiveProjectEvaluator(liveClient, liveConfiguration, NullLogger<JevActiveProjectEvaluator>.Instance);
        var liveOutcome = await liveEvaluator.EvaluateAsync(jevOpportunity, preferences, CancellationToken.None);
        Check(liveOutcome.Provider == EvaluationProvider.Jev && liveOutcome.Model.StartsWith("jev-", StringComparison.Ordinal),
            "The opt-in live contract should return a resolved Jev model and typed result.");
        Console.WriteLine($"Live Jev contract passed with model {liveOutcome.Model} and {liveOutcome.InputTokens} input tokens.");
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

static object ChoiceAnswer(string choice) => new { type = "choice", choice, probabilities = new Dictionary<string, double> { [choice] = 1 }, confidence = 1 };
static object NoulAnswer(double value) => new { type = "noul", noul = value };
static object ScoreAnswer(double value) => new { type = "score", score = value, legend = new Dictionary<string, string>(), probabilities = new Dictionary<string, double>(), confidence = 0.8 };

sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = new(responses);
    public int CallCount { get; private set; }
    public Uri? LastRequestUri { get; private set; }
    public string? LastAuthorizationScheme { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri;
        LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
        return Task.FromResult(responses.Dequeue());
    }
}
