using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

[ApiController, Route("api/admin/opportunity-radar"), Authorize(Roles = Roles.Admin)]
public partial class OpportunityRadarController(ApplicationDbContext db, IEnumerable<IOpportunityEvaluator> evaluators,
    IOpportunityImportService importService, IConfiguration configuration) : ControllerBase
{
    public record ImportBatchRequest<T>(string? ResearchAgent, [Required, MinLength(1), MaxLength(100)] IReadOnlyList<T> Items);
    public record EvaluateRequest(int[]? OpportunityIds, string? ConfirmationCode = null);
    public record EvaluationPreviewRequest(int[]? OpportunityIds);
    public record ReviewRequest(string? Decision, [StringLength(5000)] string Notes);
    public record ProspectTypeOverrideRequest(string? ProspectType);
    public record ActiveProjectPreferencesRequest(string[] PreferredProjectTypes, string[] ExcludedProjectTypes,
        decimal MinimumBudget, string IncompleteInformationTolerance, Dictionary<string, decimal>? WeightsV2);
    public record BusinessProspectPreferencesRequest(string[] PreferredIndustries, string[] ExcludedIndustries,
        string[] PreferredGeographies, string[] ExcludedGeographies, Dictionary<string, decimal>? OperationalPainWeights,
        Dictionary<string, decimal>? DigitalPresenceWeights);
    public record BusinessProfileRequest(string Positioning, string BusinessModel,
        string[] IdealCustomerTraits, string[] CoreOffers, string[] SecondaryOffers, string[] Capabilities,
        string[] EngagementModel, string[] CapacityConstraints, string[] GeographicFocus, string[] PriceBands,
        DateTime? LastReviewedAt);
    public record PreferencesRequest(ActiveProjectPreferencesRequest ActiveProject, BusinessProspectPreferencesRequest BusinessProspect,
        int DigestActiveProjectCount = 3, int DigestBusinessProspectCount = 2, BusinessProfileRequest? BusinessProfile = null);

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var opportunity = await db.Opportunities.AsNoTracking().Include(x => x.Evaluations)
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (opportunity is null) return NotFound();
        var evaluation = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        return Ok(new
        {
            opportunity.Id,
            entityType = opportunity.EntityType.ToString(),
            opportunity.Title,
            opportunity.Description,
            opportunity.SourceName,
            opportunity.SourceUrl,
            opportunity.SourceDate,
            opportunity.ExternalId,
            opportunityRating = opportunity.OpportunityRating?.ToString(),
            researchConfidence = opportunity.ResearchConfidence?.ToString(),
            opportunity.ResearchConfidenceReason,
            opportunity.ResearchAgent,
            passages = JsonSerializer.Deserialize<object>(opportunity.SourcePassagesJson),
            opportunity.DuplicateOfId,
            opportunity.IsSynthetic,
            opportunity.Notes,
            opportunity.CreatedAt,
            activeProject = opportunity.ActiveProjectDetail is null ? null : new
            {
                sourceType = opportunity.ActiveProjectDetail.DeclaredSourceType.ToString(),
                userDecision = opportunity.ActiveProjectDetail.UserDecision?.ToString(),
                opportunity.ActiveProjectDetail.Budget,
                competition = opportunity.ActiveProjectDetail.CompetitionProposals is null
                    && opportunity.ActiveProjectDetail.CompetitionInterviewing is null && opportunity.ActiveProjectDetail.CompetitionHires is null
                    ? null : new
                    {
                        proposals = opportunity.ActiveProjectDetail.CompetitionProposals,
                        interviewing = opportunity.ActiveProjectDetail.CompetitionInterviewing,
                        hires = opportunity.ActiveProjectDetail.CompetitionHires
                    },
                opportunity.ActiveProjectDetail.Fit,
                opportunity.ActiveProjectDetail.ProposalAngle,
                opportunity.ActiveProjectDetail.Risk
            },
            businessProspect = opportunity.BusinessProspectDetail is null ? null : new
            {
                businessName = opportunity.Title,
                websiteUrl = opportunity.BusinessProspectDetail.WebsiteUrl,
                normalizedWebsiteDomain = opportunity.BusinessProspectDetail.NormalizedWebsiteDomain,
                geography = opportunity.BusinessProspectDetail.Geography,
                industry = opportunity.BusinessProspectDetail.Industry,
                importedProspectType = opportunity.BusinessProspectDetail.ImportedProspectType?.ToString(),
                prospectTypeOverride = opportunity.BusinessProspectDetail.ProspectTypeOverride?.ToString(),
                userDecision = opportunity.BusinessProspectDetail.UserDecision?.ToString(),
                opportunity.BusinessProspectDetail.Fit,
                opportunity.BusinessProspectDetail.EntryOffer,
                opportunity.BusinessProspectDetail.Risk
            },
            evaluation = evaluation is null ? null : new
            {
                evaluation.Id,
                provider = evaluation.Provider.ToString(),
                status = evaluation.Status.ToString(),
                evaluation.Model,
                evaluation.QuestionSetVersion,
                recommendation = evaluation.Recommendation?.ToString(),
                priorityBand = evaluation.PriorityBand?.ToString(),
                budgetStatus = evaluation.BudgetStatus.ToString(),
                evaluation.OpportunityScore,
                evaluation.JevConfidence,
                evaluatedProspectType = evaluation.EvaluatedProspectType?.ToString(),
                evaluation.NeedsVerification,
                evaluation.RubricVersion,
                origin = evaluation.Origin.ToString(),
                effectiveWeights = JsonSerializer.Deserialize<object>(evaluation.EffectiveWeightsJson),
                result = evaluation.Status == EvaluationStatus.Failed ? null : JsonSerializer.Deserialize<object>(evaluation.ResultJson),
                evaluation.Summary,
                evaluation.NextStep,
                evaluation.InputTokens,
                evaluation.OutputTokens,
                evaluation.ErrorMessage,
                evaluation.CreatedAt
            }
        });
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(bool includeSynthetic = false, CancellationToken ct = default)
    {
        // Only the latest evaluation is ever read (see BuildCsv), so filtered-Include just that one row
        // per opportunity instead of the full, ever-growing evaluation history - same technique already
        // used by ValidateLiveBatch below.
        var opportunities = await db.Opportunities.AsNoTracking().Include(x => x.Evaluations.OrderByDescending(e => e.CreatedAt).Take(1))
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .Where(x => includeSynthetic || !x.IsSynthetic).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var preferences = await GetOrCreatePreferences(ct);
        var csv = OpportunityRadarReporting.BuildCsv(opportunities, preferences);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"hsl-opportunity-radar-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpPost("import/active-projects"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportActiveProject(ActiveProjectImportRequest request, CancellationToken ct)
    {
        if (!TryActiveProjectSourceType(request.SourceType, out var sourceType)) return BadRequest(new { message = "Invalid source type." });
        if (!ValidResearchConfidence(request.Confidence?.Level)) return BadRequest(new { message = "Confidence level must be Low, Medium, or High." });
        if (!ValidOpportunityRating(request.OpportunityRating)) return BadRequest(new { message = "Opportunity rating must be Low, Medium, or High." });
        if (!ValidUrl(request.SourceUrl)) return BadRequest(new { message = "Source URL must be an absolute http or https URL." });
        var result = await importService.ImportActiveProjectAsync(request, sourceType, false, null, ct);
        return Ok(new { result.Id, result.Created, result.Updated, result.NearDuplicateOfId });
    }

    [HttpPost("import/active-projects/batch"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportActiveProjectsBatch(ImportBatchRequest<ActiveProjectImportRequest> request, CancellationToken ct)
    {
        if (request.Items.Any(x => !TryActiveProjectSourceType(x.SourceType, out _) || !ValidResearchConfidence(x.Confidence?.Level)
                || !ValidOpportunityRating(x.OpportunityRating) || !ValidUrl(x.SourceUrl)))
            return BadRequest(new { message = "One or more items has an invalid source type, confidence level, opportunity rating, or source URL." });

        var imported = new List<object>();
        var updated = new List<object>();
        foreach (var item in request.Items)
        {
            TryActiveProjectSourceType(item.SourceType, out var sourceType);
            var withAgent = item.ResearchAgent is null && request.ResearchAgent is not null ? item with { ResearchAgent = request.ResearchAgent } : item;
            var result = await importService.ImportActiveProjectAsync(withAgent, sourceType, false, null, ct);
            if (result.Updated) updated.Add(new { result.Id, item.Title });
            else imported.Add(new { result.Id, item.Title, result.NearDuplicateOfId });
        }
        return Ok(new { imported, updated });
    }

    [HttpPost("import/business-prospects"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportBusinessProspect(BusinessProspectImportRequest request, CancellationToken ct)
    {
        if (!ValidUrl(request.WebsiteUrl)) return BadRequest(new { message = "Website URL must be an absolute http or https URL." });
        if (!ValidResearchConfidence(request.Confidence?.Level) || !ValidProspectType(request.ProspectType) || !ValidOpportunityRating(request.OpportunityRating))
            return BadRequest(new { message = "Prospect type, confidence level, or opportunity rating is invalid." });
        var result = await importService.ImportBusinessProspectAsync(request, false, null, ct);
        return Ok(new { result.Id, result.Created, result.Updated, result.NearDuplicateOfId });
    }

    [HttpPost("import/business-prospects/batch"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportBusinessProspectsBatch(ImportBatchRequest<BusinessProspectImportRequest> request, CancellationToken ct)
    {
        if (request.Items.Any(x => !ValidUrl(x.WebsiteUrl) || !ValidResearchConfidence(x.Confidence?.Level)
                || !ValidProspectType(x.ProspectType) || !ValidOpportunityRating(x.OpportunityRating)))
            return BadRequest(new { message = "One or more items has an invalid website URL, prospect type, confidence level, or opportunity rating." });

        var imported = new List<object>();
        var updated = new List<object>();
        foreach (var item in request.Items)
        {
            var withAgent = item.ResearchAgent is null && request.ResearchAgent is not null ? item with { ResearchAgent = request.ResearchAgent } : item;
            var result = await importService.ImportBusinessProspectAsync(withAgent, false, null, ct);
            if (result.Updated) updated.Add(new { result.Id, item.BusinessName });
            else imported.Add(new { result.Id, item.BusinessName, result.NearDuplicateOfId });
        }
        return Ok(new { imported, updated });
    }

    [HttpPost("samples")]
    public async Task<IActionResult> LoadSamples(CancellationToken ct)
    {
        var existing = await db.Opportunities.Where(x => x.SyntheticKey != null).Select(x => x.SyntheticKey).ToListAsync(ct);
        var created = new List<int>();
        foreach (var sample in ActiveProjectSamples().Where(x => !existing.Contains(x.Key)))
        {
            var result = await importService.ImportActiveProjectAsync(sample.Request, sample.SourceType, true, sample.Key, ct);
            if (result.Created) created.Add(result.Id);
        }
        foreach (var sample in BusinessProspectSamples().Where(x => !existing.Contains(x.Key)))
        {
            var result = await importService.ImportBusinessProspectAsync(sample.Request, true, sample.Key, ct);
            if (result.Created) created.Add(result.Id);
        }
        return Ok(new { createdCount = created.Count, ids = created });
    }

    [HttpPost("evaluation-preview")]
    public async Task<IActionResult> EvaluationPreview(EvaluationPreviewRequest request, CancellationToken ct)
    {
        const EvaluationProvider provider = EvaluationProvider.Jev;
        var ids = request.OpportunityIds?.Distinct().ToArray() ?? [];
        if (ids.Length > 100) return BadRequest(new { message = "Evaluation batches are limited to 100 records." });
        var opportunities = await db.Opportunities.AsNoTracking().Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .Where(x => ids.Length == 0 || ids.Contains(x.Id)).OrderBy(x => x.Id).Take(100).ToListAsync(ct);
        if (opportunities.Count == 0) return BadRequest(new { message = "Choose at least one opportunity to evaluate." });
        var preferences = await GetOrCreatePreferences(ct);
        var evaluatorsByType = ResolveEvaluators(provider, opportunities);
        var estimate = await EstimateBatchCost(opportunities, evaluatorsByType, preferences, ct);
        var jevAvailable = evaluators.Where(x => x.Provider == EvaluationProvider.Jev).All(x => x.IsAvailable);
        var evaluatorsAvailable = evaluatorsByType.Values.All(x => x.IsAvailable);
        var allowed = evaluatorsAvailable && estimate.EstimatedCost <= estimate.BatchCap && estimate.UsedToday + estimate.EstimatedTokens <= estimate.DailyLimit;
        var confirmationCode = ConfirmationCode(opportunities, provider, estimate.EstimatedTokens);
        return Ok(new
        {
            provider = provider.ToString(), recordCount = opportunities.Count, estimatedMaximumInputTokens = estimate.EstimatedTokens,
            estimatedMaximumCostUsd = decimal.Round(estimate.EstimatedCost, 6), maximumBatchCostUsd = estimate.BatchCap,
            rollingDailyInputTokensUsed = estimate.UsedToday, rollingDailyInputTokenLimit = estimate.DailyLimit,
            liveAvailable = jevAvailable, allowed,
            reason = !evaluatorsAvailable ? "Live Jev evaluation is not configured."
                : estimate.EstimatedCost > estimate.BatchCap ? "The estimated maximum cost exceeds the server batch ceiling."
                : estimate.UsedToday + estimate.EstimatedTokens > estimate.DailyLimit ? "The rolling daily input token limit would be exceeded." : null,
            confirmationCode
        });
    }

    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate(EvaluateRequest request, CancellationToken ct)
    {
        const EvaluationProvider provider = EvaluationProvider.Jev;
        var ids = request.OpportunityIds?.Distinct().ToArray() ?? [];
        if (ids.Length > 100) return BadRequest(new { message = "Evaluation batches are limited to 100 records." });
        var opportunities = await db.Opportunities.Include(x => x.Evaluations)
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .Where(x => ids.Length == 0 || ids.Contains(x.Id)).OrderBy(x => x.Id).Take(100).ToListAsync(ct);
        if (opportunities.Count == 0) return BadRequest(new { message = "Choose at least one opportunity to evaluate." });
        var preferences = await GetOrCreatePreferences(ct);
        var evaluatorsByType = ResolveEvaluators(provider, opportunities);
        if (evaluatorsByType.Values.Any(x => !x.IsAvailable))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Live Jev evaluation is not configured." });

        var preview = await ValidateLiveBatch(opportunities, evaluatorsByType, preferences, request.ConfirmationCode, ct);
        if (preview is not null) return preview;

        var outcomes = new ConcurrentBag<(Opportunity Opportunity, OpportunityEvaluationOutcome? Outcome, OpportunityEvaluationException? Error)>();
        await Parallel.ForEachAsync(opportunities, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (opportunity, token) =>
        {
            var evaluator = evaluatorsByType[opportunity.EntityType];
            try { outcomes.Add((opportunity, await evaluator.EvaluateAsync(opportunity, preferences, token), null)); }
            catch (OpportunityEvaluationException ex) { outcomes.Add((opportunity, null, ex)); }
        });

        var failed = 0;
        foreach (var item in outcomes)
        {
            if (item.Outcome is { } outcome)
            {
                item.Opportunity.Evaluations.Add(ToEvaluation(outcome, item.Opportunity.EntityType));
            }
            else
            {
                failed++;
                var questionSetVersion = item.Opportunity.EntityType == OpportunityEntityType.ActiveProject
                    ? JevActiveProjectEvaluator.QuestionSetVersion : JevBusinessProspectEvaluator.QuestionSetVersion;
                item.Opportunity.Evaluations.Add(new OpportunityEvaluation
                {
                    Provider = provider, Status = EvaluationStatus.Failed, Model = "jev-latest",
                    QuestionSetVersion = OpportunityRadarEngine.EffectiveQuestionSetVersion(questionSetVersion, preferences),
                    ErrorMessage = item.Error?.Message ?? "Evaluation failed.", CreatedAt = DateTime.UtcNow
                });
            }
            item.Opportunity.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { evaluatedCount = opportunities.Count - failed, failedCount = failed, provider = provider.ToString() });
    }

    [HttpPatch("{id:int}/review")]
    public async Task<IActionResult> UpdateReview(int id, ReviewRequest request, CancellationToken ct)
    {
        var opportunity = await db.Opportunities.Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (opportunity is null) return NotFound();
        if (opportunity.EntityType == OpportunityEntityType.ActiveProject)
        {
            if (!string.IsNullOrWhiteSpace(request.Decision)
                && (!Enum.TryParse<ActiveProjectDecision>(request.Decision, out var parsedActiveDecision) || !Enum.IsDefined(parsedActiveDecision)))
                return BadRequest(new { message = "Invalid decision." });
            opportunity.ActiveProjectDetail!.UserDecision = string.IsNullOrWhiteSpace(request.Decision) ? null : Enum.Parse<ActiveProjectDecision>(request.Decision);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.Decision)
                && (!Enum.TryParse<BusinessProspectDecision>(request.Decision, out var parsedProspectDecision) || !Enum.IsDefined(parsedProspectDecision)))
                return BadRequest(new { message = "Invalid decision." });
            opportunity.BusinessProspectDetail!.UserDecision = string.IsNullOrWhiteSpace(request.Decision) ? null : Enum.Parse<BusinessProspectDecision>(request.Decision);
        }
        opportunity.Notes = request.Notes.Trim();
        opportunity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var opportunity = await db.Opportunities.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (opportunity is null) return NotFound();
        db.Opportunities.Remove(opportunity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPatch("{id:int}/duplicate")]
    public async Task<IActionResult> ClearDuplicate(int id, CancellationToken ct)
    {
        var opportunity = await db.Opportunities.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (opportunity is null) return NotFound();
        opportunity.DuplicateOfId = null;
        opportunity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPatch("{id:int}/prospect-type")]
    public async Task<IActionResult> UpdateProspectType(int id, ProspectTypeOverrideRequest request, CancellationToken ct)
    {
        BusinessProspectType? value = null;
        if (!string.IsNullOrWhiteSpace(request.ProspectType))
        {
            if (!Enum.TryParse<BusinessProspectType>(request.ProspectType.Replace(" ", ""), true, out var parsed) || !Enum.IsDefined(parsed))
                return BadRequest(new { message = "Invalid prospect type." });
            value = parsed;
        }
        var opportunity = await db.Opportunities.Include(x => x.BusinessProspectDetail).Include(x => x.Evaluations)
            .SingleOrDefaultAsync(x => x.Id == id && x.EntityType == OpportunityEntityType.BusinessProspect, ct);
        if (opportunity is null) return NotFound();
        opportunity.BusinessProspectDetail!.ProspectTypeOverride = value;
        opportunity.UpdatedAt = DateTime.UtcNow;
        var latest = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (latest is { Provider: EvaluationProvider.Jev, Status: EvaluationStatus.Ready })
            AppendRecomposition(opportunity, latest, await GetOrCreatePreferences(ct));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("provider")]
    public IActionResult Provider()
    {
        var activeProjectJev = evaluators.Single(x => x.Provider == EvaluationProvider.Jev && x.SupportedEntityType == OpportunityEntityType.ActiveProject);
        var businessProspectJev = evaluators.Single(x => x.Provider == EvaluationProvider.Jev && x.SupportedEntityType == OpportunityEntityType.BusinessProspect);
        var model = configuration["TypeSafe:Model"] ?? "jev-latest";
        return Ok(new
        {
            activeProject = new { liveAvailable = activeProjectJev.IsAvailable, model },
            businessProspect = new { liveAvailable = businessProspectJev.IsAvailable, model }
        });
    }

    [HttpGet("digest")]
    public async Task<IActionResult> Digest(bool includeSynthetic = false, CancellationToken ct = default)
    {
        var preferences = await GetOrCreatePreferences(ct);
        // Only the latest evaluation is ever read (see ToSummary), so filtered-Include just that one row
        // per opportunity instead of the full, ever-growing evaluation history - same technique already
        // used by ValidateLiveBatch below. This is the admin's landing view, hit far more often than
        // Export, so it's the one where the old unbounded load mattered most.
        var opportunities = await db.Opportunities.AsNoTracking().Include(x => x.Evaluations.OrderByDescending(e => e.CreatedAt).Take(1))
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .Where(x => includeSynthetic || !x.IsSynthetic).ToListAsync(ct);

        var activeProjects = opportunities.Where(x => x.EntityType == OpportunityEntityType.ActiveProject).Select(ToSummary)
            .Where(x => x.Recommendation == OpportunityRecommendation.Pursue.ToString() && x.EvaluationStatus == EvaluationStatus.Ready.ToString() && x.UserDecision is null && !x.NeedsVerification)
            .OrderByDescending(x => x.OpportunityScore).ThenByDescending(x => x.JevConfidence).ThenByDescending(x => x.CreatedAt).Take(preferences.DigestActiveProjectCount).ToList();
        var businessProspects = opportunities.Where(x => x.EntityType == OpportunityEntityType.BusinessProspect).Select(ToSummary)
            .Where(x => x.Recommendation == OpportunityRecommendation.Prioritize.ToString() && x.EvaluationStatus == EvaluationStatus.Ready.ToString() && x.UserDecision is null && !x.NeedsVerification)
            .OrderByDescending(x => x.OpportunityScore).ThenByDescending(x => x.JevConfidence).ThenByDescending(x => x.CreatedAt).Take(preferences.DigestBusinessProspectCount).ToList();

        return Ok(new
        {
            activeProjects, activeProjectRequested = preferences.DigestActiveProjectCount, activeProjectReturned = activeProjects.Count,
            businessProspects, businessProspectRequested = preferences.DigestBusinessProspectCount, businessProspectReturned = businessProspects.Count
        });
    }

    private Dictionary<OpportunityEntityType, IOpportunityEvaluator> ResolveEvaluators(EvaluationProvider provider, IEnumerable<Opportunity> opportunities) =>
        opportunities.Select(x => x.EntityType).Distinct()
            .ToDictionary(entityType => entityType, entityType => evaluators.Single(x => x.Provider == provider && x.SupportedEntityType == entityType));

    private async Task<RadarPreferences> GetOrCreatePreferences(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Admin user ID is missing.");
        var preferences = await db.RadarPreferences.SingleOrDefaultAsync(x => x.OwnerUserId == userId, ct);
        if (preferences is not null) return preferences;
        preferences = new RadarPreferences
        {
            OwnerUserId = userId,
            ActiveProjectPreferencesJson = JsonSerializer.Serialize(new
            {
                preferredProjectTypes = new[] { "Integration", "Automation", "InternalTool", "Reporting", "Portal", "ExistingSoftware" },
                excludedProjectTypes = new[] { "FullTimeEmployment", "EquityOnly", "Unpaid" },
                minimumBudget = 2500m, incompleteInformationTolerance = "Medium",
                weightsV2 = OpportunityRadarV2.ActiveDefaults
            }),
            BusinessProspectPreferencesJson = JsonSerializer.Serialize(new
            {
                preferredIndustries = Array.Empty<string>(), excludedIndustries = Array.Empty<string>(),
                preferredGeographies = Array.Empty<string>(), excludedGeographies = Array.Empty<string>(),
                operationalPainWeights = OpportunityRadarV2.OperationalDefaults,
                digitalPresenceWeights = OpportunityRadarV2.DigitalDefaults
            }),
            BusinessProfileJson = JsonSerializer.Serialize(OpportunityRadarEngine.DefaultBusinessProfile, OpportunityRadarEngine.CamelCaseOptions),
            DigestActiveProjectCount = 3, DigestBusinessProspectCount = 2,
            UpdatedAt = DateTime.UtcNow
        };
        db.RadarPreferences.Add(preferences);
        await db.SaveChangesAsync(ct);
        return preferences;
    }

    private static OpportunityEvaluation ToEvaluation(OpportunityEvaluationOutcome outcome, OpportunityEntityType entityType) => new()
    {
        Provider = outcome.Provider, Status = EvaluationStatus.Ready, Model = outcome.Model,
        // The ": radar-v1" branch is unreachable today - only Jev evaluators are registered (see
        // Program.cs) - but EvaluationProvider.Simulated still exists so historical rows deserialize
        // correctly. Left in place rather than removed so a future Simulated-provider evaluator (if one
        // is ever reintroduced) doesn't silently fall through with no QuestionSetVersion.
        QuestionSetVersion = outcome.Provider == EvaluationProvider.Jev ? outcome.QuestionSetVersion : "radar-v1",
        Recommendation = outcome.Result.Recommendation, PriorityBand = outcome.Result.PriorityBand,
        OpportunityScore = outcome.Result.OpportunityScore, JevConfidence = outcome.Result.JevConfidence,
        EvaluatedProspectType = outcome.Result.ProspectType, NeedsVerification = outcome.Result.NeedsVerification,
        RubricVersion = outcome.Result.RubricVersion, Origin = EvaluationOrigin.ProviderRun,
        EffectiveWeightsJson = JsonSerializer.Serialize(outcome.Result.EffectiveWeights ?? new Dictionary<string, decimal>()),
        BudgetStatus = outcome.Result.BudgetStatus, AssessmentJson = outcome.AssessmentJson,
        ResultJson = OpportunityRadarEngine.Serialize(outcome.Result), ProviderResponseJson = outcome.ProviderResponseJson,
        Summary = outcome.Result.Summary, NextStep = outcome.Result.NextStep, InputTokens = outcome.InputTokens,
        OutputTokens = outcome.OutputTokens, CreatedAt = DateTime.UtcNow
    };

    private static void AppendRecomposition(Opportunity opportunity, OpportunityEvaluation latest, RadarPreferences preferences)
    {
        RadarResult? result = null;
        if (opportunity.EntityType == OpportunityEntityType.ActiveProject)
        {
            var assessment = OpportunityRadarV2.DeserializeActive(latest.AssessmentJson);
            if (assessment is not null) result = OpportunityRadarV2.ComposeActiveProject(opportunity, preferences, assessment);
        }
        else
        {
            var assessment = OpportunityRadarV2.DeserializeBusiness(latest.AssessmentJson);
            if (assessment is not null) result = OpportunityRadarV2.ComposeBusinessProspect(opportunity, preferences, assessment);
        }
        if (result is null) return;
        opportunity.Evaluations.Add(new OpportunityEvaluation
        {
            Provider = EvaluationProvider.Jev, Status = EvaluationStatus.Ready, Model = latest.Model,
            QuestionSetVersion = latest.QuestionSetVersion, AssessmentJson = latest.AssessmentJson,
            ProviderResponseJson = latest.ProviderResponseJson, ResultJson = OpportunityRadarEngine.Serialize(result),
            Recommendation = result.Recommendation, PriorityBand = result.PriorityBand, BudgetStatus = result.BudgetStatus,
            OpportunityScore = result.OpportunityScore, JevConfidence = result.JevConfidence,
            EvaluatedProspectType = result.ProspectType, NeedsVerification = result.NeedsVerification,
            RubricVersion = result.RubricVersion, Origin = EvaluationOrigin.LocalRecompose,
            SourceEvaluationId = ResolveSourceEvaluationId(latest),
            EffectiveWeightsJson = JsonSerializer.Serialize(result.EffectiveWeights ?? new Dictionary<string, decimal>()),
            Summary = result.Summary, NextStep = result.NextStep, InputTokens = latest.InputTokens,
            OutputTokens = latest.OutputTokens, CreatedAt = DateTime.UtcNow
        });
        opportunity.UpdatedAt = DateTime.UtcNow;
    }

    private static void AppendStale(Opportunity opportunity, OpportunityEvaluation latest, string summary)
    {
        opportunity.Evaluations.Add(new OpportunityEvaluation
        {
            Provider = latest.Provider, Status = EvaluationStatus.Stale, Model = latest.Model,
            QuestionSetVersion = latest.QuestionSetVersion, AssessmentJson = latest.AssessmentJson,
            ProviderResponseJson = latest.ProviderResponseJson, ResultJson = latest.ResultJson,
            Recommendation = latest.Recommendation, PriorityBand = latest.PriorityBand, BudgetStatus = latest.BudgetStatus,
            OpportunityScore = latest.OpportunityScore, JevConfidence = latest.JevConfidence,
            EvaluatedProspectType = latest.EvaluatedProspectType, NeedsVerification = true,
            RubricVersion = latest.RubricVersion, Origin = latest.Origin, SourceEvaluationId = ResolveSourceEvaluationId(latest),
            EffectiveWeightsJson = latest.EffectiveWeightsJson, Summary = summary, NextStep = "Reevaluate with Jev.",
            CreatedAt = DateTime.UtcNow
        });
    }

    // A row derived from a ProviderRun (recomposed locally, or marked stale) should always trace back to
    // that run; a row derived from an already-derived row just carries the link forward.
    private static int? ResolveSourceEvaluationId(OpportunityEvaluation latest) =>
        latest.Origin == EvaluationOrigin.ProviderRun ? latest.Id : latest.SourceEvaluationId;

    private async Task<IActionResult?> ValidateLiveBatch(IReadOnlyList<Opportunity> opportunities,
        Dictionary<OpportunityEntityType, IOpportunityEvaluator> evaluatorsByType, RadarPreferences preferences,
        string? suppliedConfirmationCode, CancellationToken ct)
    {
        var estimate = await EstimateBatchCost(opportunities, evaluatorsByType, preferences, ct);
        if (estimate.EstimatedCost > estimate.BatchCap)
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = "The estimated maximum cost exceeds the server batch ceiling." });
        if (estimate.UsedToday + estimate.EstimatedTokens > estimate.DailyLimit)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "The rolling daily Jev input token limit would be exceeded." });
        var expected = ConfirmationCode(opportunities, EvaluationProvider.Jev, estimate.EstimatedTokens);
        if (string.IsNullOrWhiteSpace(suppliedConfirmationCode) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(suppliedConfirmationCode), Encoding.UTF8.GetBytes(expected)))
            return Conflict(new { message = "Preview this exact batch and confirm its estimated maximum cost before evaluation." });
        return null;
    }

    // Shared by EvaluationPreview (which reports this to the admin before they commit) and
    // ValidateLiveBatch (which enforces it) so the two can never drift on what "the estimate" means.
    private async Task<BatchCostEstimate> EstimateBatchCost(IReadOnlyList<Opportunity> opportunities,
        Dictionary<OpportunityEntityType, IOpportunityEvaluator> evaluatorsByType, RadarPreferences preferences, CancellationToken ct)
    {
        var estimatedTokens = opportunities.Sum(x => evaluatorsByType[x.EntityType].EstimateMaximumInputTokens(x, preferences));
        var inputPrice = configuration.GetValue("OpportunityRadar:JevInputPricePerMillionTokens", 0.042m);
        var estimatedCost = estimatedTokens / 1_000_000m * inputPrice;
        var batchCap = configuration.GetValue("OpportunityRadar:JevMaximumEstimatedBatchCostUsd", 0.05m);
        var dailyLimit = configuration.GetValue("OpportunityRadar:JevDailyInputTokenLimit", 5_000_000);
        var usedToday = await db.OpportunityEvaluations.AsNoTracking()
            .Where(x => x.Provider == EvaluationProvider.Jev && x.CreatedAt >= DateTime.UtcNow.AddHours(-24) && x.InputTokens != null)
            .SumAsync(x => x.InputTokens ?? 0, ct);
        return new BatchCostEstimate(estimatedTokens, estimatedCost, batchCap, dailyLimit, usedToday);
    }

    private sealed record BatchCostEstimate(int EstimatedTokens, decimal EstimatedCost, decimal BatchCap, int DailyLimit, int UsedToday);

    private static string ConfirmationCode(IEnumerable<Opportunity> opportunities, EvaluationProvider provider, int estimatedTokens)
    {
        var material = string.Join('|', opportunities.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.UpdatedAt.Ticks}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}:{estimatedTokens}:{material}"))).ToLowerInvariant();
    }

    private static bool SetsEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(right.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);


}
