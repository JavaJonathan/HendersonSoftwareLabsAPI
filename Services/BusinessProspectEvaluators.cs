using System.Text;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public sealed class JevBusinessProspectEvaluator(HttpClient httpClient, IConfiguration configuration, ILogger<JevBusinessProspectEvaluator> logger)
    : JevEvaluatorBase(httpClient, configuration, logger)
{
    public const string QuestionSetVersion = "radar-business-prospect-jev-v2";

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
            var result = OpportunityRadarV2.ComposeBusinessProspect(opportunity, preferences, parsed.Assessment);
            logger.LogInformation("Jev evaluation completed for opportunity {OpportunityId} with model {Model}, input tokens {InputTokens}, output tokens {OutputTokens}",
                opportunity.Id, parsed.Model, parsed.InputTokens, parsed.OutputTokens);
            return new OpportunityEvaluationOutcome(Provider, parsed.Model, OpportunityRadarV2.Serialize(parsed.Assessment), result,
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
            ["prospect_type"] = Choice(
                "Classify the opportunity using only the supplied evidence. Do not infer operational pain from industry norms or from a weak website.",
                new Dictionary<string, object?>
                {
                    ["operational_pain"] = "Direct evidence supports a costly, repetitive, software-addressable workflow problem.",
                    ["digital_presence"] = "The supported opportunity is primarily a weak website or digital customer experience.",
                    ["hybrid"] = "Direct operational pain exists and digital weakness offers an additional entry path.",
                    ["unknown"] = "The evidence does not support one of the other classifications."
                }),
            ["pain_evidence"] = Score("How strong and direct is the evidence of costly or repetitive operational pain?", fourPoint),
            ["automation_feasibility"] = Score("How feasible is a narrow software, automation, or integration response without replacing a core system?", fourPoint),
            ["economic_leverage"] = Score("How plausible is meaningful economic leverage? Do not treat a full salary as recoverable savings or invent ROI.", fourPoint),
            ["contained_engagement"] = Score("How plausible is a contained first engagement HSL could deliver?", fourPoint),
            ["urgency"] = Score("How strong is the direct evidence of urgency or favorable timing?", fourPoint),
            ["hsl_delivery_fit"] = Score("How well does the opportunity fit a small custom-software consultancy focused on integrations, automation, portals, reporting, and web applications?", fourPoint),
            ["buyer_access"] = Score("How reachable and identifiable is a likely buyer or decision-maker?", fourPoint),
            ["business_strength"] = Score("How established does the business appear from real-world signals? Judge the business, not its website.", fourPoint),
            ["digital_weakness"] = Score("How weak is the observable digital presence relative to the business?", fourPoint),
            ["reputation_mismatch"] = Score("How large is the gap between real-world reputation and the digital presence?", fourPoint),
            ["entry_project_strength"] = Score("How plausible and well-scoped is a first digital-presence engagement?", fourPoint),
            ["speculative_workflow"] = Noul("Are the claimed workflow problems supported mainly by industry assumptions rather than direct observed evidence?"),
            ["physical_or_judgment_heavy"] = Noul("Is the work primarily physical, relationship-based, judgment-heavy, or dominated by unpredictable exceptions?"),
            ["core_system_replacement"] = Noul("Would the likely solution require replacing a specialized core ERP, dispatch, medical, financial, or similar system?"),
            ["primary_evidence"] = Choice("Choose the passage that best supports the primary opportunity judgment. Choose none when unsupported.", evidenceCriteria),
            ["concern_evidence"] = Choice("Choose the passage that best supports any concern. Choose none when there is no concern.", evidenceCriteria)
        };
        return JsonSerializer.Serialize(new { state, model = Model, questions });
    }

    private static (string Model, BusinessProspectV2Assessment Assessment, int InputTokens, int OutputTokens) ParseResponse(string json, Opportunity opportunity)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var resolvedModel = RequiredString(root, "model");
        var answers = root.GetProperty("answers");
        var usage = root.GetProperty("usage");
        var prospectType = ChoiceValue(answers, "prospect_type") switch
        {
            "operational_pain" => BusinessProspectType.OperationalPain,
            "digital_presence" => BusinessProspectType.DigitalPresence,
            "hybrid" => BusinessProspectType.Hybrid,
            _ => BusinessProspectType.Unknown
        };
        var passageIds = (OpportunityRadarEngine.DeserializePassages(opportunity.SourcePassagesJson)).Select(x => x.Id).ToHashSet();
        passageIds.Add("none");
        var evidence = ValidateEvidenceChoice(answers, "primary_evidence", passageIds);
        var factors = new Dictionary<string, JevJudgment>
        {
            ["painEvidence"] = Judgment(answers, "pain_evidence", evidence),
            ["automationFeasibility"] = Judgment(answers, "automation_feasibility", evidence),
            ["economicLeverage"] = Judgment(answers, "economic_leverage", evidence),
            ["containedEngagement"] = Judgment(answers, "contained_engagement", evidence),
            ["urgency"] = Judgment(answers, "urgency", evidence),
            ["hslDeliveryFit"] = Judgment(answers, "hsl_delivery_fit", evidence),
            ["buyerAccess"] = Judgment(answers, "buyer_access", evidence),
            ["businessStrength"] = Judgment(answers, "business_strength", evidence),
            ["digitalWeakness"] = Judgment(answers, "digital_weakness", evidence),
            ["reputationMismatch"] = Judgment(answers, "reputation_mismatch", evidence),
            ["entryProjectStrength"] = Judgment(answers, "entry_project_strength", evidence)
        };
        var assessment = new BusinessProspectV2Assessment(prospectType, ConfidenceValue(answers, "prospect_type"), factors,
            NoulValue(answers, "speculative_workflow") >= 0.67, NoulValue(answers, "physical_or_judgment_heavy") >= 0.67,
            NoulValue(answers, "core_system_replacement") >= 0.67, ValidateEvidenceChoice(answers, "concern_evidence", passageIds));
        return (resolvedModel, assessment, usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32());
    }

    private static JevJudgment Judgment(JsonElement answers, string key, string evidence) =>
        new(Math.Clamp(ScoreValue(answers, key), 0, 3), ConfidenceValue(answers, key), evidence);
}
