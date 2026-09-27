using System.Text;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public sealed class JevBusinessProspectEvaluator(HttpClient httpClient, IConfiguration configuration, ILogger<JevBusinessProspectEvaluator> logger)
    : JevEvaluatorBase(httpClient, configuration, logger)
{
    public const string QuestionSetVersion = "radar-business-prospect-jev-v8";

    public override OpportunityEntityType SupportedEntityType => OpportunityEntityType.BusinessProspect;

    public override int EstimateMaximumInputTokens(Opportunity opportunity, RadarPreferences preferences)
        => Encoding.UTF8.GetByteCount(BuildRequestJson(opportunity, preferences));

    // Screening preferences remain post-hoc scoring inputs. The HSL business profile (capabilities
    // included) always enters the provider request, there is no draft/inactive state - changing it
    // makes prior results stale (see OpportunityRadarController.UpdatePreferences).
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
            return new OpportunityEvaluationOutcome(Provider, parsed.Model, OpportunityRadarEngine.EffectiveQuestionSetVersion(QuestionSetVersion, preferences),
                OpportunityRadarV2.Serialize(parsed.Assessment), result,
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
        foreach (var passage in passages) evidenceCriteria[passage.Id] = Describe(passage);
        var detail = opportunity.BusinessProspectDetail;
        var state = new Dictionary<string, object?>
        {
            ["business_name"] = opportunity.Title,
            ["industry"] = detail?.Industry,
            ["geography"] = detail?.Geography,
            ["passages"] = passages.ToDictionary(x => x.Id, Describe),
            ["hsl_capabilities"] = OpportunityRadarEngine.ReadCapabilities(preferences),
            ["hsl_business_profile"] = OpportunityRadarEngine.ReadBusinessProfileContext(preferences)
        };
        var fourPoint = new[] { "No evidence", "Weak or ambiguous", "Useful initial evidence", "Clear and concrete" };
        const string profileRule = " Use hsl_business_profile to understand HSL's actual offers, delivery model, constraints, and price bands. It is business context, not evidence that this prospect has a need. Keep confidence low when prospect evidence is missing.";
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
            ["pain_cost_severity"] = Score("How strong and direct is the evidence that this workflow problem is costly: wasted money, staff time, or missed revenue?", fourPoint),
            ["pain_cost_severity_passage"] = Choice("Choose the passage that best supports the pain_cost_severity score. Choose none when unsupported.", evidenceCriteria),
            ["pain_frequency"] = Score("How strong and direct is the evidence that this workflow problem is frequent or repetitive, rather than a one-off?", fourPoint),
            ["pain_frequency_passage"] = Choice("Choose the passage that best supports the pain_frequency score. Choose none when unsupported.", evidenceCriteria),
            ["automation_feasibility"] = Score("How feasible is a narrow software, automation, or integration response without replacing a core system?", fourPoint),
            ["automation_feasibility_passage"] = Choice("Choose the passage that best supports the automation_feasibility score. Choose none when unsupported.", evidenceCriteria),
            ["economic_leverage"] = Score("How plausible is meaningful economic leverage? Do not treat a full salary as recoverable savings or invent ROI." + profileRule, fourPoint),
            ["economic_leverage_passage"] = Choice("Choose the passage that best supports the economic_leverage score. Choose none when unsupported.", evidenceCriteria),
            ["contained_engagement"] = Score("How plausible is a contained first engagement HSL could deliver?" + profileRule, fourPoint),
            ["contained_engagement_passage"] = Choice("Choose the passage that best supports the contained_engagement score. Choose none when unsupported.", evidenceCriteria),
            ["urgency"] = Score("How strong is the direct evidence of urgency or favorable timing?", fourPoint),
            ["urgency_passage"] = Choice("Choose the passage that best supports the urgency score. Choose none when unsupported.", evidenceCriteria),
            // No _passage companion: this is a judgment about whether the work matches HSL's own stated
            // capabilities, not something an observed business fact would typically demonstrate. Asking
            // for a citation here just made Jev cite a weak passage or, more often, cite none while still
            // scoring high from general context - triggering an "unsupported" check that was noise, not
            // signal (see AddCommonChecks in OpportunityRadarV2.cs).
            ["hsl_delivery_fit"] = Score("How well does the opportunity fit the listed hsl_capabilities?" + profileRule, fourPoint),
            ["buyer_access"] = Score("How reachable and identifiable is a likely buyer or decision-maker?", fourPoint),
            ["buyer_access_passage"] = Choice("Choose the passage that best supports the buyer_access score. Choose none when unsupported.", evidenceCriteria),
            ["business_strength"] = Score("How established does the business appear from real-world signals? Judge the business, not its website.", fourPoint),
            ["business_strength_passage"] = Choice("Choose the passage that best supports the business_strength score. Choose none when unsupported.", evidenceCriteria),
            ["digital_weakness"] = Score("How weak is the observable digital presence relative to the business?", fourPoint),
            ["digital_weakness_passage"] = Choice("Choose the passage that best supports the digital_weakness score. Choose none when unsupported.", evidenceCriteria),
            ["reputation_mismatch"] = Score("How large is the gap between real-world reputation and the digital presence?", fourPoint),
            ["reputation_mismatch_passage"] = Choice("Choose the passage that best supports the reputation_mismatch score. Choose none when unsupported.", evidenceCriteria),
            ["entry_project_strength"] = Score("How plausible and well-scoped is a first digital-presence engagement?" + profileRule, fourPoint),
            ["entry_project_strength_passage"] = Choice("Choose the passage that best supports the entry_project_strength score. Choose none when unsupported.", evidenceCriteria),
            ["speculative_workflow"] = Noul("Are the claimed workflow problems supported mainly by industry assumptions rather than direct observed evidence?"),
            ["physical_or_judgment_heavy"] = Noul("Is the work primarily physical, relationship-based, judgment-heavy, or dominated by unpredictable exceptions?"),
            ["core_system_replacement"] = Noul("Would the likely solution require replacing a specialized core ERP, dispatch, medical, financial, or similar system?"),
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
        var factors = new Dictionary<string, JevJudgment>
        {
            ["painEvidence"] = CombineJudgments(Judgment(answers, "pain_cost_severity", passageIds), Judgment(answers, "pain_frequency", passageIds)),
            ["automationFeasibility"] = Judgment(answers, "automation_feasibility", passageIds),
            ["economicLeverage"] = Judgment(answers, "economic_leverage", passageIds),
            ["containedEngagement"] = Judgment(answers, "contained_engagement", passageIds),
            ["urgency"] = Judgment(answers, "urgency", passageIds),
            ["hslDeliveryFit"] = JudgmentWithoutPassage(answers, "hsl_delivery_fit"),
            ["buyerAccess"] = Judgment(answers, "buyer_access", passageIds),
            ["businessStrength"] = Judgment(answers, "business_strength", passageIds),
            ["digitalWeakness"] = Judgment(answers, "digital_weakness", passageIds),
            ["reputationMismatch"] = Judgment(answers, "reputation_mismatch", passageIds),
            ["entryProjectStrength"] = Judgment(answers, "entry_project_strength", passageIds)
        };
        var assessment = new BusinessProspectV2Assessment(prospectType, ConfidenceValue(answers, "prospect_type"), factors,
            NoulValue(answers, "speculative_workflow") >= OpportunityRadarThresholds.NoulYesThreshold,
            NoulValue(answers, "physical_or_judgment_heavy") >= OpportunityRadarThresholds.NoulYesThreshold,
            NoulValue(answers, "core_system_replacement") >= OpportunityRadarThresholds.NoulYesThreshold, ValidateEvidenceChoice(answers, "concern_evidence", passageIds));
        return (resolvedModel, assessment, usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32());
    }

    private static JevJudgment Judgment(JsonElement answers, string key, HashSet<string> passageIds) =>
        new(Math.Clamp(ScoreValue(answers, key), 0, 3), ConfidenceValue(answers, key), ValidateEvidenceChoice(answers, $"{key}_passage", passageIds));

    // For factors with no _passage question at all (currently just hsl_delivery_fit - see BuildRequestJson).
    private static JevJudgment JudgmentWithoutPassage(JsonElement answers, string key) =>
        new(Math.Clamp(ScoreValue(answers, key), 0, 3), ConfidenceValue(answers, key), "none");

    // Surfaces the sourcing agent's own category tag (see EvidenceFact.Category) inline with the passage
    // text, so Jev has an explicit hint for which factor a passage was collected for. It's a hint, not a
    // filter - every passage is still offered for every factor, and Jev is free to disagree with the tag.
    private static string Describe(RadarPassage passage) =>
        string.IsNullOrWhiteSpace(passage.Category) ? passage.Text : $"[{passage.Category}] {passage.Text}";
}
