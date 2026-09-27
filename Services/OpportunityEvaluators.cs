using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Entities;

namespace HendersonSoftwareLabsAPI.Services;

public record OpportunityEvaluationOutcome(
    EvaluationProvider Provider,
    string Model,
    string AssessmentJson,
    RadarResult Result,
    string ProviderResponseJson,
    int? InputTokens,
    int? OutputTokens);

public interface IOpportunityEvaluator
{
    EvaluationProvider Provider { get; }
    OpportunityEntityType SupportedEntityType { get; }
    bool IsAvailable { get; }
    int EstimateMaximumInputTokens(Opportunity opportunity, RadarPreferences preferences);
    Task<OpportunityEvaluationOutcome> EvaluateAsync(Opportunity opportunity, RadarPreferences preferences, CancellationToken ct);
}

public sealed class OpportunityEvaluationException(string code, string message, HttpStatusCode? providerStatus = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public HttpStatusCode? ProviderStatus { get; } = providerStatus;
}

public abstract class JevEvaluatorBase(HttpClient httpClient, IConfiguration configuration, ILogger logger) : IOpportunityEvaluator
{
    private const string DefaultModel = "jev-latest";
    protected readonly string? ApiKey = configuration["TypeSafe:ApiKey"];
    protected readonly string Model = configuration["TypeSafe:Model"] ?? DefaultModel;

    public EvaluationProvider Provider => EvaluationProvider.Jev;
    public abstract OpportunityEntityType SupportedEntityType { get; }
    public bool IsAvailable => !string.IsNullOrWhiteSpace(ApiKey);

    public abstract int EstimateMaximumInputTokens(Opportunity opportunity, RadarPreferences preferences);
    public abstract Task<OpportunityEvaluationOutcome> EvaluateAsync(Opportunity opportunity, RadarPreferences preferences, CancellationToken ct);

    protected async Task<string> SendWithRetriesAsync(string requestJson, int opportunityId, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new OpportunityEvaluationException("key_missing", "Live Jev evaluation is not configured.");

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/systemone")
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                logger.LogWarning("Jev transport failure for opportunity {OpportunityId}, retry {RetryNumber}", opportunityId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new OpportunityEvaluationException("transport_error", "Jev could not be reached after retries.", null, ex);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 2)
            {
                logger.LogWarning("Jev timed out for opportunity {OpportunityId}, retry {RetryNumber}", opportunityId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), ct);
                continue;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new OpportunityEvaluationException("timeout", "Jev did not respond before the configured timeout after retries.", null, ex);
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode == 529)
            {
                if (attempt < 2)
                {
                    var delay = RetryDelay(response, attempt);
                    logger.LogWarning("Jev returned status {ProviderStatus} for opportunity {OpportunityId}, retry {RetryNumber}",
                        (int)response.StatusCode, opportunityId, attempt + 1);
                    response.Dispose();
                    await Task.Delay(delay, ct);
                    continue;
                }
                var status = response.StatusCode;
                response.Dispose();
                throw new OpportunityEvaluationException("provider_busy", "Jev remained rate limited or overloaded after retries.", status);
            }
            break;
        }

        if (response is null)
            throw new OpportunityEvaluationException("transport_error", "Jev did not return a response.");

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                var code = status == HttpStatusCode.Unauthorized ? "authentication_error"
                    : status == HttpStatusCode.UnprocessableEntity ? "request_validation_error" : "provider_error";
                logger.LogWarning("Jev returned status {ProviderStatus} for opportunity {OpportunityId}", (int)status, opportunityId);
                throw new OpportunityEvaluationException(code, "Jev rejected the evaluation request.", status);
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    protected static OpportunityEvaluationException WrapParseFailure(Exception ex) => ex switch
    {
        JsonException => new OpportunityEvaluationException("invalid_response", "Jev returned a response that did not match the expected schema.", null, ex),
        InvalidDataException => new OpportunityEvaluationException("invalid_response", ex.Message, null, ex),
        KeyNotFoundException or InvalidOperationException or FormatException or OverflowException =>
            new OpportunityEvaluationException("invalid_response", "Jev returned a response that did not match the expected schema.", null, ex),
        _ => new OpportunityEvaluationException("invalid_response", "Jev returned a response that did not match the expected schema.", null, ex)
    };

    protected static string ValidateEvidenceChoice(JsonElement answers, string key, HashSet<string> passageIds)
    {
        var value = ChoiceValue(answers, key);
        if (!passageIds.Contains(value)) throw new InvalidDataException("Jev returned an evidence reference that is not in the stored source.");
        return value;
    }

    // Combines sub-questions that split a single conflated judgment (e.g. pain_evidence's cost vs.
    // frequency) back into one factor, so weights/checks downstream never see the split. Evidence comes
    // from whichever sub-question scored higher, since that's the half actually driving the combined
    // score and what the unsupported:<factor> check cares about.
    protected static JevJudgment CombineJudgments(params JevJudgment[] parts) =>
        new(parts.Average(x => x.Score), parts.Average(x => x.Confidence),
            parts.OrderByDescending(x => x.Score).First().EvidencePassageId);

    protected static string ChoiceValue(JsonElement answers, string key)
    {
        var answer = answers.GetProperty(key);
        if (RequiredString(answer, "type") != "choice") throw new InvalidDataException($"Jev answer {key} has the wrong type.");
        var choice = RequiredString(answer, "choice");
        if (!answer.TryGetProperty("probabilities", out var probabilities) || probabilities.ValueKind != JsonValueKind.Object
            || !probabilities.TryGetProperty(choice, out _))
            throw new InvalidDataException($"Jev answer {key} is missing choice probabilities.");
        foreach (var probability in probabilities.EnumerateObject())
        {
            if (!probability.Value.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 1)
                throw new InvalidDataException($"Jev answer {key} has an invalid choice probability.");
        }
        _ = ConfidenceValue(answers, key);
        return choice;
    }

    protected static double ScoreValue(JsonElement answers, string key)
    {
        var answer = answers.GetProperty(key);
        if (RequiredString(answer, "type") != "score") throw new InvalidDataException($"Jev answer {key} has the wrong type.");
        var value = answer.GetProperty("score").GetDouble();
        if (!double.IsFinite(value) || value is < 0 or > 3) throw new InvalidDataException($"Jev answer {key} has an invalid score.");
        return value;
    }

    protected static double ConfidenceValue(JsonElement answers, string key)
    {
        var answer = answers.GetProperty(key);
        if (!answer.TryGetProperty("confidence", out var confidence) || !confidence.TryGetDouble(out var value))
            throw new InvalidDataException($"Jev answer {key} is missing confidence.");
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new InvalidDataException($"Jev answer {key} has invalid confidence.");
        return value;
    }

    protected static double NoulValue(JsonElement answers, string key)
    {
        var answer = answers.GetProperty(key);
        if (RequiredString(answer, "type") != "noul") throw new InvalidDataException($"Jev answer {key} has the wrong type.");
        var value = answer.GetProperty("noul").GetDouble();
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new InvalidDataException($"Jev answer {key} has an invalid Noul probability.");
        return value;
    }

    protected static string RequiredString(JsonElement element, string property)
        => element.GetProperty(property).GetString() ?? throw new InvalidDataException($"Jev response field {property} is missing.");

    protected static object Choice(string instructions, Dictionary<string, object?> criteria) => new { type = "choice", instructions, criteria };
    protected static object Score(string instructions, string[] criteria) => new { type = "score", instructions, criteria };
    protected static object Noul(string instructions) => new { type = "noul", instructions, criteria = new { @true = "The condition is supported by the source.", @false = "The condition is not supported by the source." } };

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        var bounded = retryAfter ?? TimeSpan.FromMilliseconds(500 * (1 << attempt));
        if (bounded < TimeSpan.Zero) bounded = TimeSpan.Zero;
        return bounded > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : bounded;
    }
}

public sealed class JevActiveProjectEvaluator(HttpClient httpClient, IConfiguration configuration, ILogger<JevActiveProjectEvaluator> logger)
    : JevEvaluatorBase(httpClient, configuration, logger)
{
    public const string QuestionSetVersion = "radar-active-project-jev-v4";

    public override OpportunityEntityType SupportedEntityType => OpportunityEntityType.ActiveProject;

    public override int EstimateMaximumInputTokens(Opportunity opportunity, RadarPreferences preferences)
        => Encoding.UTF8.GetByteCount(BuildRequestJson(opportunity, preferences));

    public override async Task<OpportunityEvaluationOutcome> EvaluateAsync(Opportunity opportunity, RadarPreferences preferences, CancellationToken ct)
    {
        var requestJson = BuildRequestJson(opportunity, preferences);
        var responseJson = await SendWithRetriesAsync(requestJson, opportunity.Id, ct);
        try
        {
            var parsed = ParseResponse(responseJson, opportunity);
            var result = OpportunityRadarV2.ComposeActiveProject(opportunity, preferences, parsed.Assessment);
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
        var prefs = OpportunityRadarEngine.ReadActiveProjectPreferences(preferences);
        var declaredSourceType = opportunity.ActiveProjectDetail?.DeclaredSourceType ?? ActiveProjectSourceType.ExplicitDemand;
        var state = new
        {
            title = opportunity.Title,
            declared_source_type = declaredSourceType.ToString(),
            passages = passages.ToDictionary(x => x.Id, x => x.Text),
            hsl_capabilities = prefs.Capabilities
        };
        var fourPoint = new[] { "No evidence", "Weak or ambiguous", "Useful initial evidence", "Clear and concrete" };
        var questions = new Dictionary<string, object>
        {
            ["opportunity_kind"] = Choice("Classify the opportunity by the evidence. Do not infer buying intent from manual work.", new Dictionary<string, object?>
            {
                ["explicit_demand"] = "A person or organization directly requests software or automation help.",
                ["operational_signal"] = "Manual work is described without a stated intention to buy software.",
                ["full_time_role"] = "This is employment or staffing rather than an independent project.",
                ["other"] = "None of the other options clearly apply."
            }),
            ["project_type"] = Choice("Choose the closest primary project category.", new Dictionary<string, object?>
            {
                ["integration"] = "Connect or synchronize systems or data.", ["automation"] = "Remove repeated manual workflow steps.",
                ["internal_tool"] = "Build a tool for employees or back-office users.", ["reporting"] = "Reporting, dashboards, or data exports.",
                ["portal"] = "Authenticated customer or partner portal.", ["existing_software"] = "Improve or repair existing custom software.",
                ["greenfield_product"] = "Build a new commercial software product.", ["staffing"] = "Employment or staff augmentation.", ["other"] = null
            }),
            ["problem_clarity"] = Score("How clearly does the source establish a concrete software-related problem and desired outcome?", fourPoint),
            ["problem_clarity_passage"] = Choice("Choose the passage that best supports the problem_clarity score. Choose none when unsupported.", evidenceCriteria),
            ["hsl_delivery_fit"] = Score("How well does the work fit the listed hsl_capabilities? Judge the work, not keyword overlap.", fourPoint),
            ["hsl_delivery_fit_passage"] = Choice("Choose the passage that best supports the hsl_delivery_fit score. Choose none when unsupported.", evidenceCriteria),
            ["independent_scope_value"] = Score("How likely is a version scoped for one independent engineer to still be genuinely useful to the buyer?", fourPoint),
            ["independent_scope_value_passage"] = Choice("Choose the passage that best supports the independent_scope_value score. Choose none when unsupported.", evidenceCriteria),
            ["independent_scope_feasibility"] = Score("How feasible is it for one experienced independent engineer to build a first version in a reasonable timeframe?", fourPoint),
            ["independent_scope_feasibility_passage"] = Choice("Choose the passage that best supports the independent_scope_feasibility score. Choose none when unsupported.", evidenceCriteria),
            ["economic_viability"] = Score("How plausible is meaningful economic value relative to a contained software engagement? Do not invent ROI.", fourPoint),
            ["economic_viability_passage"] = Choice("Choose the passage that best supports the economic_viability score. Choose none when unsupported.", evidenceCriteria),
            ["urgency"] = Score("How strong is the direct evidence of timing or urgency?", fourPoint),
            ["urgency_passage"] = Choice("Choose the passage that best supports the urgency score. Choose none when unsupported.", evidenceCriteria),
            ["buyer_readiness_access"] = Score("How reachable and identifiable is a likely buyer or decision-maker?", fourPoint),
            ["buyer_readiness_access_passage"] = Choice("Choose the passage that best supports the buyer_readiness_access score. Choose none when unsupported.", evidenceCriteria),
            ["buyer_readiness_next_step"] = Score("How clear and concrete is an identifiable next step to engage the buyer?", fourPoint),
            ["buyer_readiness_next_step_passage"] = Choice("Choose the passage that best supports the buyer_readiness_next_step score. Choose none when unsupported.", evidenceCriteria),
            ["information_market_fit_market"] = Score("How sufficient is the source for understanding the market and buyer context needed for an initial decision?", fourPoint),
            ["information_market_fit_market_passage"] = Choice("Choose the passage that best supports the information_market_fit_market score. Choose none when unsupported.", evidenceCriteria),
            ["information_market_fit_delivery"] = Score("How sufficient is the source for understanding the delivery context, such as scope or technical requirements, needed for an initial decision?", fourPoint),
            ["information_market_fit_delivery_passage"] = Choice("Choose the passage that best supports the information_market_fit_delivery score. Choose none when unsupported.", evidenceCriteria),
            ["employment_or_staffing"] = Noul("Is this primarily employment, staff augmentation, or an ongoing role rather than an independent project?"),
            ["team_scale"] = Noul("Does success appear to require a large team, broad transformation, or multi-year delivery?"),
            ["core_system_replacement"] = Noul("Does the request appear to require replacing a specialized core ERP, dispatch, medical, financial, or similar system rather than complementing it?"),
            ["concern_evidence"] = Choice("Choose the single passage that best supports any delivery concern. Choose none when there is no concern.", evidenceCriteria)
        };
        return JsonSerializer.Serialize(new { state, model = Model, questions });
    }

    private static (string Model, ActiveProjectV2Assessment Assessment, int InputTokens, int OutputTokens) ParseResponse(string json, Opportunity opportunity)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var resolvedModel = RequiredString(root, "model");
        var answers = root.GetProperty("answers");
        var usage = root.GetProperty("usage");
        var kind = ChoiceValue(answers, "opportunity_kind") switch
        {
            "explicit_demand" => ActiveProjectKind.ExplicitDemand,
            "operational_signal" => ActiveProjectKind.OperationalSignal,
            "full_time_role" => ActiveProjectKind.FullTimeRole,
            _ => ActiveProjectKind.Other
        };
        var projectType = ChoiceValue(answers, "project_type") switch
        {
            "integration" => "Integration", "automation" => "Automation", "internal_tool" => "InternalTool",
            "reporting" => "Reporting", "portal" => "Portal", "existing_software" => "ExistingSoftware",
            "greenfield_product" => "GreenfieldProduct", "staffing" => "FullTimeEmployment", _ => "Other"
        };
        var passageIds = (OpportunityRadarEngine.DeserializePassages(opportunity.SourcePassagesJson)).Select(x => x.Id).ToHashSet();
        passageIds.Add("none");
        var factors = new Dictionary<string, JevJudgment>
        {
            ["problemClarity"] = Judgment(answers, "problem_clarity", passageIds),
            ["hslDeliveryFit"] = Judgment(answers, "hsl_delivery_fit", passageIds),
            ["independentScope"] = CombineJudgments(Judgment(answers, "independent_scope_value", passageIds), Judgment(answers, "independent_scope_feasibility", passageIds)),
            ["economicViability"] = Judgment(answers, "economic_viability", passageIds),
            ["urgency"] = Judgment(answers, "urgency", passageIds),
            ["buyerReadiness"] = CombineJudgments(Judgment(answers, "buyer_readiness_access", passageIds), Judgment(answers, "buyer_readiness_next_step", passageIds)),
            ["informationMarketFit"] = CombineJudgments(Judgment(answers, "information_market_fit_market", passageIds), Judgment(answers, "information_market_fit_delivery", passageIds))
        };
        var assessment = new ActiveProjectV2Assessment(kind.ToString(), ConfidenceValue(answers, "opportunity_kind"), projectType, factors,
            NoulValue(answers, "employment_or_staffing") >= OpportunityRadarThresholds.NoulYesThreshold,
            NoulValue(answers, "team_scale") >= OpportunityRadarThresholds.NoulYesThreshold,
            NoulValue(answers, "core_system_replacement") >= OpportunityRadarThresholds.NoulYesThreshold, ValidateEvidenceChoice(answers, "concern_evidence", passageIds));
        return (resolvedModel, assessment, usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32());
    }

    private static JevJudgment Judgment(JsonElement answers, string key, HashSet<string> passageIds) =>
        new(Math.Clamp(ScoreValue(answers, key), 0, 3), ConfidenceValue(answers, key), ValidateEvidenceChoice(answers, $"{key}_passage", passageIds));
}
