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

public sealed class SimulatedActiveProjectEvaluator : IOpportunityEvaluator
{
    public EvaluationProvider Provider => EvaluationProvider.Simulated;
    public OpportunityEntityType SupportedEntityType => OpportunityEntityType.ActiveProject;
    public bool IsAvailable => true;

    public int EstimateMaximumInputTokens(Opportunity opportunity, RadarPreferences preferences) => 0;

    public Task<OpportunityEvaluationOutcome> EvaluateAsync(Opportunity opportunity, RadarPreferences preferences, CancellationToken ct)
    {
        var (result, assessment) = OpportunityRadarEngine.EvaluateActiveProject(opportunity, preferences);
        return Task.FromResult(new OpportunityEvaluationOutcome(Provider, "simulation-v1", OpportunityRadarEngine.Serialize(assessment), result, "{}", null, null));
    }
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

    protected static string ChoiceValue(JsonElement answers, string key)
    {
        var answer = answers.GetProperty(key);
        if (RequiredString(answer, "type") != "choice") throw new InvalidDataException($"Jev answer {key} has the wrong type.");
        return RequiredString(answer, "choice");
    }

    protected static double ScoreValue(JsonElement answers, string key)
    {
        var answer = answers.GetProperty(key);
        if (RequiredString(answer, "type") != "score") throw new InvalidDataException($"Jev answer {key} has the wrong type.");
        return answer.GetProperty("score").GetDouble();
    }

    protected static double NoulValue(JsonElement answers, string key)
    {
        var answer = answers.GetProperty(key);
        if (RequiredString(answer, "type") != "noul") throw new InvalidDataException($"Jev answer {key} has the wrong type.");
        return answer.GetProperty("noul").GetDouble();
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
    public const string QuestionSetVersion = "radar-active-project-jev-v1";

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
            var result = OpportunityRadarEngine.ComposeActiveProject(opportunity, preferences, parsed.Assessment,
                ["Opportunity kind and fit are Jev model judgments, not facts or a probability of winning work."]);
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
            ["problem_concreteness"] = Noul("Is there a concrete software-related business problem with a recognizable outcome? Vague requests without an outcome are false."),
            ["capability_fit"] = Score("How well does the work itself fit the listed `hsl_capabilities`? Judge transferable work, not keyword overlap alone.",
                ["No meaningful fit", "Adjacent but weak fit", "Good fit", "Strong fit with demonstrated HSL strengths"]),
            ["solo_feasibility"] = Noul("Could one experienced independent engineer plausibly own and deliver a useful first version? Full-time, multi-year, or large-team roles are false."),
            ["information_sufficiency"] = Score("How much useful information is present for an initial opportunity review? Do not penalize only because budget or timeline is absent.", fourPoint),
            ["dependency_risk"] = Choice("Choose the most important delivery dependency visible in the source.", new Dictionary<string, object?>
            {
                ["none"] = "No material dependency is stated.", ["unclear"] = "A dependency may exist but is not clear.",
                ["third_party_api"] = "Access, capability, documentation, or approval for an external API is uncertain.",
                ["enterprise_team_scale"] = "Success depends on a large team, broad transformation, or ongoing staffing."
            }),
            ["problem_evidence"] = Choice("Choose the single passage that best supports the problem-concreteness judgment. Choose none when no passage supports it.", evidenceCriteria),
            ["fit_evidence"] = Choice("Choose the single passage that best supports the capability-fit judgment. Choose none when no passage supports it.", evidenceCriteria),
            ["concern_evidence"] = Choice("Choose the single passage that best supports the scope or dependency concern. Choose none when there is no concern.", evidenceCriteria)
        };
        return JsonSerializer.Serialize(new { state, model = Model, questions });
    }

    private static (string Model, ActiveProjectAssessment Assessment, int InputTokens, int OutputTokens) ParseResponse(string json, Opportunity opportunity)
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
        var problem = NoulValue(answers, "problem_concreteness") >= 0.67 ? 3 : NoulValue(answers, "problem_concreteness") >= 0.4 ? 2 : 1;
        var fit = (int)Math.Round(ScoreValue(answers, "capability_fit"), MidpointRounding.AwayFromZero);
        var scope = NoulValue(answers, "solo_feasibility") >= 0.67 ? 3 : NoulValue(answers, "solo_feasibility") >= 0.4 ? 2 : 0;
        var information = (int)Math.Round(ScoreValue(answers, "information_sufficiency"), MidpointRounding.AwayFromZero);
        var dependency = ChoiceValue(answers, "dependency_risk") switch
        {
            "third_party_api" => "ThirdPartyApi", "enterprise_team_scale" => "EnterpriseTeamScale",
            "unclear" => "Unclear", _ => "None"
        };
        var passageIds = (OpportunityRadarEngine.DeserializePassages(opportunity.SourcePassagesJson)).Select(x => x.Id).ToHashSet();
        passageIds.Add("none");
        var assessment = new ActiveProjectAssessment(kind, projectType, Math.Clamp(problem, 0, 3), Math.Clamp(fit, 0, 3), Math.Clamp(scope, 0, 3),
            Math.Clamp(information, 0, 3), dependency, ValidateEvidenceChoice(answers, "problem_evidence", passageIds),
            ValidateEvidenceChoice(answers, "fit_evidence", passageIds), ValidateEvidenceChoice(answers, "concern_evidence", passageIds));
        return (resolvedModel, assessment, usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32());
    }
}
