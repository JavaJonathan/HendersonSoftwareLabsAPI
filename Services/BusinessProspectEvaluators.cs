using System.Text;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public sealed class SimulatedBusinessProspectEvaluator : IOpportunityEvaluator
{
    public EvaluationProvider Provider => EvaluationProvider.Simulated;
    public OpportunityEntityType SupportedEntityType => OpportunityEntityType.BusinessProspect;
    public bool IsAvailable => true;

    public int EstimateMaximumInputTokens(Opportunity opportunity, RadarPreferences preferences) => 0;

    public Task<OpportunityEvaluationOutcome> EvaluateAsync(Opportunity opportunity, RadarPreferences preferences, CancellationToken ct)
    {
        var (result, assessment) = OpportunityRadarEngine.EvaluateBusinessProspect(opportunity, preferences);
        return Task.FromResult(new OpportunityEvaluationOutcome(Provider, "simulation-v1", OpportunityRadarEngine.Serialize(assessment), result, "{}", null, null));
    }
}

public sealed class JevBusinessProspectEvaluator(HttpClient httpClient, IConfiguration configuration, ILogger<JevBusinessProspectEvaluator> logger)
    : JevEvaluatorBase(httpClient, configuration, logger)
{
    public const string QuestionSetVersion = "radar-business-prospect-jev-v1";

    public override OpportunityEntityType SupportedEntityType => OpportunityEntityType.BusinessProspect;

    public override int EstimateMaximumInputTokens(Opportunity opportunity, RadarPreferences preferences)
        => Encoding.UTF8.GetByteCount(BuildRequestJson(opportunity, preferences));

    // State is deliberately business_name/industry/geography/passages only - no preferences-derived
    // fields. That means no BusinessProspect preference change can ever make a live Jev result
    // stale; every preference change here is pure post-hoc scoring recompose.
    public override async Task<OpportunityEvaluationOutcome> EvaluateAsync(Opportunity opportunity, RadarPreferences preferences, CancellationToken ct)
    {
        var requestJson = BuildRequestJson(opportunity, preferences);
        var responseJson = await SendWithRetriesAsync(requestJson, opportunity.Id, ct);
        try
        {
            var parsed = ParseResponse(responseJson, opportunity);
            var result = OpportunityRadarEngine.ComposeBusinessProspect(opportunity, preferences, parsed.Assessment,
                ["Business strength, digital-presence weakness, and entry-project judgments are Jev model judgments, not facts or a probability of winning work."]);
            logger.LogInformation("Jev evaluation completed for opportunity {OpportunityId} with model {Model}, input tokens {InputTokens}, output tokens {OutputTokens}",
                opportunity.Id, parsed.Model, parsed.InputTokens, parsed.OutputTokens);
            return new OpportunityEvaluationOutcome(Provider, parsed.Model, OpportunityRadarEngine.Serialize(parsed.Assessment), result,
                responseJson, parsed.InputTokens, parsed.OutputTokens);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw WrapParseFailure(ex);
        }
    }

    internal string BuildRequestJson(Opportunity opportunity, RadarPreferences preferences)
    {
        var passages = OpportunityRadarEngine.DeserializePassages(opportunity.SourcePassagesJson);
        var evidenceCriteria = new Dictionary<string, object?> { ["none"] = "No passage directly supports the judgment." };
        foreach (var passage in passages) evidenceCriteria[passage.Id] = passage.Text;
        var detail = opportunity.BusinessProspectDetail;
        var state = new
        {
            business_name = opportunity.Title,
            industry = detail?.Industry,
            geography = detail?.Geography,
            passages = passages.ToDictionary(x => x.Id, x => x.Text)
        };
        var fourPoint = new[] { "No evidence", "Weak or ambiguous", "Useful initial evidence", "Clear and concrete" };
        var questions = new Dictionary<string, object>
        {
            ["buying_intent"] = Choice(
                "Classify buying intent using only explicit evidence (a request for proposals or quotes, a help-wanted listing implying the gap, a stated intention to hire or buy software). A weak, outdated, or broken website alone is never evidence of buying intent or an internal workflow problem.",
                new Dictionary<string, object?>
                {
                    ["unknown"] = "No explicit buying-intent evidence exists.",
                    ["weak"] = "Some ambiguous or indirect signal exists.",
                    ["moderate"] = "A plausible but unconfirmed signal exists.",
                    ["strong"] = "Explicit, direct evidence of intent to buy or hire exists."
                }),
            ["business_strength"] = Score("How strong and established does this business appear from real-world signals (reviews, longevity, staff size, visible traction)? Judge the business itself, never its website.", fourPoint),
            ["digital_presence_weakness"] = Score("How weak or outdated is the observable digital presence relative to a business of this evident size? Higher score means a larger observable gap.", fourPoint),
            ["reputation_website_mismatch"] = Score("How large is the gap between real-world reputation and what the website conveys?", fourPoint),
            ["entry_project_strength"] = Score("How plausible and well-scoped is an initial engagement (e.g. a website rebuild, a booking system, a basic SEO fix) that this business could act on without deep internal integration?", fourPoint),
            ["contactability"] = Score("How reachable is a decision-maker at this business from the evidence (working contact page, listed phone or email, named owner)?", fourPoint),
            ["evidence_completeness"] = Score("How much verifiable evidence is present for an initial review? Do not penalize only because buying intent is absent.", fourPoint),
            ["entry_project_evidence"] = Choice("Choose the single passage that best supports the entry-project judgment. Choose none when no passage supports it.", evidenceCriteria),
            ["reputation_evidence"] = Choice("Choose the single passage that best supports the reputation or business-strength judgment. Choose none when no passage supports it.", evidenceCriteria),
            ["contact_evidence"] = Choice("Choose the single passage that best supports the contactability judgment. Choose none when no passage supports it.", evidenceCriteria)
        };
        return JsonSerializer.Serialize(new { state, model = Model, questions });
    }

    private static (string Model, BusinessProspectAssessment Assessment, int InputTokens, int OutputTokens) ParseResponse(string json, Opportunity opportunity)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var resolvedModel = RequiredString(root, "model");
        var answers = root.GetProperty("answers");
        var usage = root.GetProperty("usage");
        var buyingIntent = ChoiceValue(answers, "buying_intent") switch
        {
            "weak" => "Weak", "moderate" => "Moderate", "strong" => "Strong", _ => "Unknown"
        };
        var businessStrength = (int)Math.Round(ScoreValue(answers, "business_strength"), MidpointRounding.AwayFromZero);
        var digitalWeakness = (int)Math.Round(ScoreValue(answers, "digital_presence_weakness"), MidpointRounding.AwayFromZero);
        var reputationMismatch = (int)Math.Round(ScoreValue(answers, "reputation_website_mismatch"), MidpointRounding.AwayFromZero);
        var entryProject = (int)Math.Round(ScoreValue(answers, "entry_project_strength"), MidpointRounding.AwayFromZero);
        var contactability = (int)Math.Round(ScoreValue(answers, "contactability"), MidpointRounding.AwayFromZero);
        var evidenceCompleteness = (int)Math.Round(ScoreValue(answers, "evidence_completeness"), MidpointRounding.AwayFromZero);
        var passageIds = (OpportunityRadarEngine.DeserializePassages(opportunity.SourcePassagesJson)).Select(x => x.Id).ToHashSet();
        passageIds.Add("none");
        var assessment = new BusinessProspectAssessment(buyingIntent, Math.Clamp(businessStrength, 0, 3), Math.Clamp(digitalWeakness, 0, 3),
            Math.Clamp(reputationMismatch, 0, 3), Math.Clamp(entryProject, 0, 3), Math.Clamp(contactability, 0, 3), Math.Clamp(evidenceCompleteness, 0, 3),
            ValidateEvidenceChoice(answers, "entry_project_evidence", passageIds), ValidateEvidenceChoice(answers, "reputation_evidence", passageIds),
            ValidateEvidenceChoice(answers, "contact_evidence", passageIds));
        return (resolvedModel, assessment, usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32());
    }
}
