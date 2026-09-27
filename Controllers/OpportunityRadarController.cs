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
public class OpportunityRadarController(ApplicationDbContext db, IEnumerable<IOpportunityEvaluator> evaluators,
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

    [HttpGet]
    public async Task<IActionResult> List(string entityType = "All", string recommendation = "All", string sourceType = "All",
        string decision = "All", string prospectType = "All", string verification = "All", string? query = null, int page = 1, CancellationToken ct = default)
    {
        const int pageSize = 25;
        if (page < 1 || page > 1_000_000) return BadRequest(new { message = "Invalid page." });
        if (!TryEntityTypeFilter(entityType, out var entityTypeFilter)) return BadRequest(new { message = "Invalid entity type." });
        IActionResult EmptyList() => Ok(new { items = Array.Empty<object>(), total = 0, page, pageSize });

        OpportunityRecommendation? recommendationFilter = null;
        if (!recommendation.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<OpportunityRecommendation>(recommendation, true, out var parsedRecommendation) || !Enum.IsDefined(parsedRecommendation))
                return EmptyList();
            recommendationFilter = parsedRecommendation;
        }

        ActiveProjectSourceType? sourceTypeFilter = null;
        if (!sourceType.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryActiveProjectSourceType(sourceType, out var parsedSourceType)) return EmptyList();
            sourceTypeFilter = parsedSourceType;
        }

        var wantsUnreviewed = decision.Equals("Unreviewed", StringComparison.OrdinalIgnoreCase);
        ActiveProjectDecision? activeDecisionFilter = null;
        BusinessProspectDecision? prospectDecisionFilter = null;
        if (!decision.Equals("All", StringComparison.OrdinalIgnoreCase) && !wantsUnreviewed)
        {
            var hasActive = Enum.TryParse<ActiveProjectDecision>(decision, true, out var parsedActive) && Enum.IsDefined(parsedActive);
            var hasProspect = Enum.TryParse<BusinessProspectDecision>(decision, true, out var parsedProspect) && Enum.IsDefined(parsedProspect);
            if (!hasActive && !hasProspect) return EmptyList();
            if (hasActive) activeDecisionFilter = parsedActive;
            if (hasProspect) prospectDecisionFilter = parsedProspect;
        }

        // Only each opportunity's latest evaluation is ever used, so project it via a correlated
        // subquery instead of Include()-ing the full (and, per reimport/preferences-save, ever-growing)
        // evaluation history. Filtering, ordering, and paging all happen in SQL, not after ToListAsync.
        var projected = db.Opportunities.AsNoTracking()
            .Where(x => entityTypeFilter == null || x.EntityType == entityTypeFilter)
            .Select(x => new
            {
                x.Id, x.EntityType, x.Title, x.Description, x.DuplicateOfId, x.IsSynthetic, x.CreatedAt,
                SourceType = x.ActiveProjectDetail != null ? x.ActiveProjectDetail.DeclaredSourceType : (ActiveProjectSourceType?)null,
                ActiveDecision = x.ActiveProjectDetail != null ? x.ActiveProjectDetail.UserDecision : null,
                ProspectDecision = x.BusinessProspectDetail != null ? x.BusinessProspectDetail.UserDecision : null,
                Industry = x.BusinessProspectDetail != null ? x.BusinessProspectDetail.Industry : null,
                Geography = x.BusinessProspectDetail != null ? x.BusinessProspectDetail.Geography : null,
                WebsiteDomain = x.BusinessProspectDetail != null ? x.BusinessProspectDetail.NormalizedWebsiteDomain : null,
                ImportedProspectType = x.BusinessProspectDetail != null ? x.BusinessProspectDetail.ImportedProspectType : null,
                ProspectTypeOverride = x.BusinessProspectDetail != null ? x.BusinessProspectDetail.ProspectTypeOverride : null,
                Latest = x.Evaluations.OrderByDescending(e => e.CreatedAt)
                    .Select(e => new { e.Recommendation, e.PriorityBand, e.BudgetStatus, e.Summary, e.Status, e.Provider,
                        e.OpportunityScore, e.JevConfidence, e.EvaluatedProspectType, e.NeedsVerification })
                    .FirstOrDefault()
            });

        if (recommendationFilter is not null)
            projected = projected.Where(x => x.Latest != null && x.Latest.Recommendation == recommendationFilter);
        if (sourceTypeFilter is not null)
            projected = projected.Where(x => x.SourceType == sourceTypeFilter);
        if (!prospectType.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<BusinessProspectType>(prospectType.Replace(" ", ""), true, out var parsedType) || !Enum.IsDefined(parsedType)) return EmptyList();
            projected = projected.Where(x => x.Latest != null && (x.ProspectTypeOverride ?? x.Latest.EvaluatedProspectType ?? x.ImportedProspectType) == parsedType);
        }
        if (verification.Equals("NeedsVerification", StringComparison.OrdinalIgnoreCase))
            projected = projected.Where(x => x.Latest != null && x.Latest.NeedsVerification);
        else if (verification.Equals("Clear", StringComparison.OrdinalIgnoreCase))
            projected = projected.Where(x => x.Latest != null && !x.Latest.NeedsVerification);
        else if (!verification.Equals("All", StringComparison.OrdinalIgnoreCase)) return EmptyList();
        if (wantsUnreviewed)
            projected = projected.Where(x => x.ActiveDecision == null && x.ProspectDecision == null);
        else if (activeDecisionFilter is not null || prospectDecisionFilter is not null)
            projected = projected.Where(x => (activeDecisionFilter != null && x.ActiveDecision == activeDecisionFilter)
                || (prospectDecisionFilter != null && x.ProspectDecision == prospectDecisionFilter));
        if (!string.IsNullOrWhiteSpace(query))
            projected = projected.Where(x => EF.Functions.ILike(x.Title, $"%{query}%") || EF.Functions.ILike(x.Description, $"%{query}%"));

        var total = await projected.CountAsync(ct);
        var pageItems = await projected
            .OrderByDescending(x => x.Latest != null && x.Latest.Status == EvaluationStatus.Ready && x.Latest.OpportunityScore != null)
            .ThenByDescending(x => x.Latest != null ? x.Latest.OpportunityScore ?? -1m : -1m)
            .ThenByDescending(x => x.Latest != null ? x.Latest.JevConfidence ?? -1m : -1m)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // A second, PK-bounded query for just the >= 25 rows on this page, kept separate from `projected`
        // above (which also backs the Count query) so the top-factor JSON blob is never at risk of being
        // read for the whole filtered set, only for the page actually returned. See the "List no longer
        // loads every opportunity and its full evaluation history into memory" fix this pairs with.
        var pageIds = pageItems.Select(x => x.Id).ToList();
        var resultJsonByOpportunityId = await db.Opportunities.AsNoTracking()
            .Where(x => pageIds.Contains(x.Id))
            .Select(x => new { x.Id, ResultJson = x.Evaluations.OrderByDescending(e => e.CreatedAt).Select(e => e.ResultJson).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Id, x => x.ResultJson, ct);

        var items = pageItems.Select(x => new
        {
            x.Id, entityType = x.EntityType.ToString(), x.Title,
            preview = OpportunityRadarEngine.Preview(x.Description),
            sourceType = x.EntityType == OpportunityEntityType.ActiveProject ? x.SourceType?.ToString() : null,
            recommendation = x.Latest?.Recommendation?.ToString(), priorityBand = x.Latest?.PriorityBand?.ToString(),
            budgetStatus = x.EntityType == OpportunityEntityType.ActiveProject ? (x.Latest?.BudgetStatus ?? BudgetStatus.Unknown).ToString() : null,
            summary = x.Latest?.Summary, evaluationStatus = x.Latest?.Status.ToString(), evaluationProvider = x.Latest?.Provider.ToString(),
            opportunityScore = x.Latest?.OpportunityScore, jevConfidence = x.Latest?.JevConfidence,
            prospectType = OpportunityRadarEngine.ResolvedProspectType(x.ProspectTypeOverride, x.Latest?.EvaluatedProspectType, x.ImportedProspectType)?.ToString(),
            needsVerification = x.Latest?.NeedsVerification ?? false,
            topFactorLabel = TopFactorLabel(resultJsonByOpportunityId.GetValueOrDefault(x.Id)),
            userDecision = x.EntityType == OpportunityEntityType.ActiveProject ? x.ActiveDecision?.ToString() : x.ProspectDecision?.ToString(),
            x.DuplicateOfId, x.IsSynthetic, x.CreatedAt,
            industry = x.EntityType == OpportunityEntityType.ActiveProject ? null : x.Industry,
            geography = x.EntityType == OpportunityEntityType.ActiveProject ? null : x.Geography,
            websiteDomain = x.EntityType == OpportunityEntityType.ActiveProject ? null : x.WebsiteDomain
        });
        return Ok(new { items, total, page, pageSize });
    }

    // The factor whose (score / 100 * effective weight) contributes most to the total OpportunityScore -
    // i.e. what is actually driving the number, not just the highest raw factor score (a high score on a
    // near-zero-weight factor barely moves the total; a moderate score on a heavily-weighted one can move
    // it a lot). Defensive like OpportunityRadarV2.DeserializeActive/DeserializeBusiness: a malformed or
    // pre-factor-rubric ResultJson (or a null-scored Business Prospect with no factors at all) just omits
    // the label rather than failing the whole list request.
    private static string? TopFactorLabel(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<RadarResult>(resultJson, OpportunityRadarEngine.CaseInsensitiveOptions);
            if (result?.Factors is null || result.EffectiveWeights is null) return null;
            return result.Factors
                .Where(factor => result.EffectiveWeights.ContainsKey(factor.Key))
                .OrderByDescending(factor => factor.Score / 100.0 * (double)result.EffectiveWeights[factor.Key])
                .FirstOrDefault()?.Label;
        }
        catch (JsonException) { return null; }
    }

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
        if (!ValidUrl(request.SourceUrl)) return BadRequest(new { message = "Source URL must be an absolute http or https URL." });
        var result = await importService.ImportActiveProjectAsync(request, sourceType, false, null, ct);
        return Ok(new { result.Id, result.Created, result.Updated, result.NearDuplicateOfId });
    }

    [HttpPost("import/active-projects/batch"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportActiveProjectsBatch(ImportBatchRequest<ActiveProjectImportRequest> request, CancellationToken ct)
    {
        if (request.Items.Any(x => !TryActiveProjectSourceType(x.SourceType, out _) || !ValidResearchConfidence(x.Confidence?.Level) || !ValidUrl(x.SourceUrl)))
            return BadRequest(new { message = "One or more items has an invalid source type, confidence level, or source URL." });

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
        if (!ValidResearchConfidence(request.Confidence?.Level) || !ValidProspectType(request.ProspectType))
            return BadRequest(new { message = "Prospect type or confidence level is invalid." });
        var result = await importService.ImportBusinessProspectAsync(request, false, null, ct);
        return Ok(new { result.Id, result.Created, result.Updated, result.NearDuplicateOfId });
    }

    [HttpPost("import/business-prospects/batch"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportBusinessProspectsBatch(ImportBatchRequest<BusinessProspectImportRequest> request, CancellationToken ct)
    {
        if (request.Items.Any(x => !ValidUrl(x.WebsiteUrl) || !ValidResearchConfidence(x.Confidence?.Level) || !ValidProspectType(x.ProspectType)))
            return BadRequest(new { message = "One or more items has an invalid website URL, prospect type, or confidence level." });

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

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken ct) => Ok(ToPreferencesResponse(await GetOrCreatePreferences(ct)));

    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences(PreferencesRequest request, CancellationToken ct)
    {
        if (request.ActiveProject.MinimumBudget < 0
            || !Enum.TryParse<IncompleteInformationTolerance>(request.ActiveProject.IncompleteInformationTolerance, out var parsedTolerance) || !Enum.IsDefined(parsedTolerance)
            || !ValidWeights(request.ActiveProject.WeightsV2, OpportunityRadarV2.ActiveDefaults.Keys)
            || !ValidWeights(request.BusinessProspect.OperationalPainWeights, OpportunityRadarV2.OperationalDefaults.Keys)
            || !ValidWeights(request.BusinessProspect.DigitalPresenceWeights, OpportunityRadarV2.DigitalDefaults.Keys)
            || request.BusinessProfile is { } suppliedProfile && !ValidBusinessProfile(suppliedProfile)
            || request.DigestActiveProjectCount is < 0 or > 25 || request.DigestBusinessProspectCount is < 0 or > 25)
            return BadRequest(new { message = "Invalid screening preferences." });

        var preferences = await GetOrCreatePreferences(ct);
        var oldCapabilities = OpportunityRadarEngine.ReadCapabilities(preferences);
        var oldBusinessProfileDigest = OpportunityRadarEngine.BusinessProfileDigest(preferences);
        var oldActiveJson = preferences.ActiveProjectPreferencesJson;
        var oldBusinessJson = preferences.BusinessProspectPreferencesJson;
        preferences.ActiveProjectPreferencesJson = JsonSerializer.Serialize(new
        {
            preferredProjectTypes = request.ActiveProject.PreferredProjectTypes.Distinct(StringComparer.OrdinalIgnoreCase),
            excludedProjectTypes = request.ActiveProject.ExcludedProjectTypes.Distinct(StringComparer.OrdinalIgnoreCase),
            minimumBudget = request.ActiveProject.MinimumBudget,
            incompleteInformationTolerance = request.ActiveProject.IncompleteInformationTolerance,
            weightsV2 = request.ActiveProject.WeightsV2
        });
        var activeProjectPreferencesChanged = oldActiveJson != preferences.ActiveProjectPreferencesJson;

        preferences.BusinessProspectPreferencesJson = JsonSerializer.Serialize(new
        {
            preferredIndustries = request.BusinessProspect.PreferredIndustries.Distinct(StringComparer.OrdinalIgnoreCase),
            excludedIndustries = request.BusinessProspect.ExcludedIndustries.Distinct(StringComparer.OrdinalIgnoreCase),
            preferredGeographies = request.BusinessProspect.PreferredGeographies.Distinct(StringComparer.OrdinalIgnoreCase),
            excludedGeographies = request.BusinessProspect.ExcludedGeographies.Distinct(StringComparer.OrdinalIgnoreCase),
            operationalPainWeights = request.BusinessProspect.OperationalPainWeights,
            digitalPresenceWeights = request.BusinessProspect.DigitalPresenceWeights
        });
        var businessProspectPreferencesChanged = oldBusinessJson != preferences.BusinessProspectPreferencesJson;
        if (request.BusinessProfile is { } businessProfile)
        {
            preferences.BusinessProfileJson = JsonSerializer.Serialize(new HslBusinessProfile(
                businessProfile.Positioning.Trim(), businessProfile.BusinessModel.Trim(),
                Clean(businessProfile.IdealCustomerTraits), Clean(businessProfile.CoreOffers), Clean(businessProfile.SecondaryOffers),
                Clean(businessProfile.Capabilities), Clean(businessProfile.EngagementModel), Clean(businessProfile.CapacityConstraints),
                Clean(businessProfile.GeographicFocus), Clean(businessProfile.PriceBands), businessProfile.LastReviewedAt),
                OpportunityRadarEngine.CamelCaseOptions);
        }
        var businessProfilePromptChanged = oldBusinessProfileDigest != OpportunityRadarEngine.BusinessProfileDigest(preferences);
        var capabilitiesChanged = !SetsEqual(oldCapabilities, OpportunityRadarEngine.ReadCapabilities(preferences));
        preferences.DigestActiveProjectCount = request.DigestActiveProjectCount;
        preferences.DigestBusinessProspectCount = request.DigestBusinessProspectCount;
        preferences.UpdatedAt = DateTime.UtcNow;

        // Only a Ready/Jev latest evaluation can ever be recomposed or made stale below, so filter to
        // those opportunities in SQL first instead of materializing the entire table (and every
        // evaluation row for every opportunity) on every preferences save.
        var eligibleIds = await db.Opportunities.AsNoTracking()
            .Select(x => new { x.Id, Latest = x.Evaluations.OrderByDescending(e => e.CreatedAt).Select(e => new { e.Provider, e.Status }).FirstOrDefault() })
            .Where(x => x.Latest != null && x.Latest.Provider == EvaluationProvider.Jev && x.Latest.Status == EvaluationStatus.Ready)
            .Select(x => x.Id).ToListAsync(ct);
        // eligibleIds already pinned down which opportunity has a Ready/Jev latest evaluation, so this
        // only needs to load that one row per opportunity (not the whole, ever-growing history) to append
        // to via AppendRecomposition/AppendStale below.
        var opportunities = await db.Opportunities.Include(x => x.Evaluations.OrderByDescending(e => e.CreatedAt).Take(1))
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .Where(x => eligibleIds.Contains(x.Id)).ToListAsync(ct);
        foreach (var opportunity in opportunities)
        {
            var latest = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (latest is null || latest.Provider != EvaluationProvider.Jev || latest.Status != EvaluationStatus.Ready) continue;

            if (businessProfilePromptChanged)
            {
                AppendStale(opportunity, latest, "The HSL business profile changed. Run Jev again before relying on this evaluation.");
                continue;
            }
            if (capabilitiesChanged)
            {
                AppendStale(opportunity, latest, "Shared HSL capabilities changed. Run Jev again before relying on this evaluation.");
                continue;
            }
            var relevantChanged = opportunity.EntityType == OpportunityEntityType.ActiveProject
                ? activeProjectPreferencesChanged : businessProspectPreferencesChanged;
            if (relevantChanged) AppendRecomposition(opportunity, latest, preferences);
        }
        await db.SaveChangesAsync(ct);
        return Ok(ToPreferencesResponse(preferences));
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

    private static object ToPreferencesResponse(RadarPreferences value)
    {
        var activeProject = OpportunityRadarEngine.ReadActiveProjectPreferences(value);
        var businessProspect = OpportunityRadarEngine.ReadBusinessProspectPreferences(value);
        var businessProfile = OpportunityRadarEngine.ReadBusinessProfile(value);
        return new
        {
            activeProject = new
            {
                activeProject.PreferredProjectTypes, activeProject.ExcludedProjectTypes,
                activeProject.MinimumBudget,
                incompleteInformationTolerance = activeProject.IncompleteInformationTolerance.ToString(),
                weightsV2 = OpportunityRadarV2.ReadActiveWeights(value)
            },
            businessProspect = new
            {
                businessProspect.PreferredIndustries, businessProspect.ExcludedIndustries,
                businessProspect.PreferredGeographies, businessProspect.ExcludedGeographies,
                operationalPainWeights = OpportunityRadarV2.ReadOperationalWeights(value),
                digitalPresenceWeights = OpportunityRadarV2.ReadDigitalWeights(value)
            },
            businessProfile,
            digestActiveProjectCount = value.DigestActiveProjectCount, digestBusinessProspectCount = value.DigestBusinessProspectCount,
            value.UpdatedAt
        };
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

    private static bool ValidWeights(Dictionary<string, decimal>? weights, IEnumerable<string> requiredKeys)
    {
        if (weights is null) return true;
        var required = requiredKeys.ToArray();
        return weights.Count == required.Length && required.All(weights.ContainsKey)
            && weights.Values.All(x => x is >= 0 and <= 100);
    }

    private static bool ValidBusinessProfile(BusinessProfileRequest profile) =>
        profile.Positioning is not null && profile.Positioning.Trim().Length is > 0 and <= 1000
        && profile.BusinessModel is not null && profile.BusinessModel.Trim().Length is > 0 and <= 1000
        && profile.IdealCustomerTraits is not null && profile.CoreOffers is not null && profile.SecondaryOffers is not null
        && profile.Capabilities is not null && profile.EngagementModel is not null && profile.CapacityConstraints is not null
        && profile.GeographicFocus is not null && profile.PriceBands is not null
        && ValidProfileList(profile.IdealCustomerTraits) && ValidProfileList(profile.CoreOffers)
        && ValidProfileList(profile.SecondaryOffers, allowEmpty: true) && ValidProfileList(profile.Capabilities)
        && ValidProfileList(profile.EngagementModel) && ValidProfileList(profile.CapacityConstraints)
        && ValidProfileList(profile.GeographicFocus) && ValidProfileList(profile.PriceBands);

    private static bool ValidProfileList(string[] values, bool allowEmpty = false) =>
        values.Length <= 30 && (allowEmpty || values.Length > 0)
        && values.All(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length <= 500);

    private static string[] Clean(IEnumerable<string> values) => values.Select(x => x.Trim())
        .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static SummaryRow ToSummary(Opportunity opportunity)
    {
        var evaluation = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        var isActiveProject = opportunity.EntityType == OpportunityEntityType.ActiveProject;
        return new SummaryRow(opportunity.Id, opportunity.EntityType.ToString(), opportunity.Title,
            OpportunityRadarEngine.Preview(opportunity.Description),
            isActiveProject ? opportunity.ActiveProjectDetail?.DeclaredSourceType.ToString() : null,
            evaluation?.Recommendation?.ToString(), evaluation?.PriorityBand?.ToString(),
            isActiveProject ? (evaluation?.BudgetStatus ?? BudgetStatus.Unknown).ToString() : null,
            evaluation?.Summary, evaluation?.Status.ToString(), evaluation?.Provider.ToString(),
            isActiveProject ? opportunity.ActiveProjectDetail?.UserDecision?.ToString() : opportunity.BusinessProspectDetail?.UserDecision?.ToString(),
            opportunity.DuplicateOfId, opportunity.IsSynthetic, opportunity.CreatedAt,
            isActiveProject ? null : opportunity.BusinessProspectDetail?.Industry,
            isActiveProject ? null : opportunity.BusinessProspectDetail?.Geography,
            isActiveProject ? null : opportunity.BusinessProspectDetail?.NormalizedWebsiteDomain,
            evaluation?.OpportunityScore, evaluation?.JevConfidence,
            isActiveProject ? null : OpportunityRadarEngine.ResolvedProspectType(opportunity.BusinessProspectDetail?.ProspectTypeOverride,
                evaluation?.EvaluatedProspectType, opportunity.BusinessProspectDetail?.ImportedProspectType)?.ToString(),
            evaluation?.NeedsVerification ?? false);
    }

    private sealed record SummaryRow(int Id, string EntityType, string Title, string Preview, string? SourceType, string? Recommendation,
        string? PriorityBand, string? BudgetStatus, string? Summary, string? EvaluationStatus, string? EvaluationProvider,
        string? UserDecision, int? DuplicateOfId, bool IsSynthetic, DateTime CreatedAt, string? Industry, string? Geography, string? WebsiteDomain,
        decimal? OpportunityScore, decimal? JevConfidence, string? ProspectType, bool NeedsVerification);

    private static int PriorityOrder(string? value) => value switch { "High" => 0, "Medium" => 1, "Low" => 2, _ => 3 };
    private static bool ValidUrl(string? value) => string.IsNullOrWhiteSpace(value)
        || Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    private static bool TryActiveProjectSourceType(string value, out ActiveProjectSourceType sourceType) =>
        Enum.TryParse(value.Replace("_", "", StringComparison.Ordinal), true, out sourceType) && Enum.IsDefined(sourceType);
    private static bool TryEntityTypeFilter(string value, out OpportunityEntityType? entityType)
    {
        entityType = null;
        if (value.Equals("All", StringComparison.OrdinalIgnoreCase)) return true;
        if (Enum.TryParse<OpportunityEntityType>(value, true, out var parsed)) { entityType = parsed; return true; }
        return false;
    }

    private static bool ValidResearchConfidence(string? value)
    {
        try { _ = OpportunityImportService.ParseResearchConfidence(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidProspectType(string? value)
    {
        try { _ = OpportunityImportService.ParseProspectType(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static IReadOnlyList<(string Key, ActiveProjectSourceType SourceType, ActiveProjectImportRequest Request)> ActiveProjectSamples() =>
    [
        ("integration-strong", ActiveProjectSourceType.ExplicitDemand, new("Shopify supplier reconciliation",
            "We need a tool that reconciles Shopify orders with supplier spreadsheets, flags mismatches, and produces a daily exception report within 8 weeks.",
            "ExplicitDemand", Budget: "$8,000")),
        ("automation-semantic", ActiveProjectSourceType.ExplicitDemand, new("Remove daily order rekeying",
            "Our operations coordinator copies new orders from email attachments into three vendor systems every morning. We want the repeated entry removed and failures surfaced for review.",
            "ExplicitDemand", Budget: "$6,000")),
        ("keyword-bad-scope", ActiveProjectSourceType.ExplicitDemand, new("Enterprise React .NET transformation",
            "Seeking a full-time principal engineer to lead a multi-year enterprise React, .NET, SQL, and AWS transformation with a team of twelve developers. Salary and employee benefits provided.",
            "ExplicitDemand")),
        ("unknown-budget", ActiveProjectSourceType.ExplicitDemand, new("Customer reporting portal",
            "Build a secure customer portal where clients can view monthly SQL-backed reports and download approved exports. We have a clear design and would like delivery in 10 weeks.",
            "ExplicitDemand")),
        ("low-budget", ActiveProjectSourceType.ExplicitDemand, new("Inventory dashboard",
            "Create a React inventory dashboard connected to our existing API. The deadline is two weeks.",
            "ExplicitDemand", Budget: "$500")),
        ("full-time-role", ActiveProjectSourceType.ExplicitDemand, new("Senior software engineer",
            "Full-time employee role for a senior C# and React engineer. This position is 40 hours per week and includes salary, health insurance, and paid leave.",
            "ExplicitDemand")),
        ("vague-request", ActiveProjectSourceType.ExplicitDemand, new("Need an app",
            "We need an app to make our business better. Please send a quote.",
            "ExplicitDemand")),
        ("api-risk", ActiveProjectSourceType.ExplicitDemand, new("Legacy vendor synchronization",
            "Build an integration that synchronizes customer records with our legacy vendor.",
            "ExplicitDemand", Budget: "$9,000", Risk: "API access is pending vendor approval and the undocumented API may not expose update operations.")),
        ("operational-signal", ActiveProjectSourceType.OperationalSignal, new("Daily spreadsheet order processing",
            "Synthetic company job description: the operations specialist downloads orders, rekeys them into supplier spreadsheets, and emails exception reports each day.",
            "OperationalSignal")),
        ("integration-near-duplicate", ActiveProjectSourceType.ExplicitDemand, new("Shopify order and supplier reconciliation",
            "We need a tool to reconcile Shopify orders against supplier spreadsheets, highlight mismatches, and send a daily exception report with an 8 week delivery target.",
            "ExplicitDemand", Budget: "$8,000"))
    ];

    private static IReadOnlyList<(string Key, BusinessProspectImportRequest Request)> BusinessProspectSamples() =>
    [
        ("prospect-strong", new("Riverside Family Dental",
            [new EvidenceFact("Established dental practice, well known locally, 5-star reviews and loyal customers for over fifteen years.", "https://example-riverside-dental.test", null),
             new EvidenceFact("The website is outdated, not mobile friendly, and has no online booking system.", "https://example-riverside-dental.test", null)],
            "https://example-riverside-dental.test", "Local", "Healthcare",
            Fit: "The practice needs a new website with online booking.")),
        ("prospect-already-modern", new("Crestline Auto Body",
            [new EvidenceFact("Well-regarded auto body shop with a modern website, mobile friendly, recently redesigned, with online booking already in place.", "https://example-crestline-autobody.test", null)],
            "https://example-crestline-autobody.test", "Local", "Automotive")),
        ("prospect-unknown-intent", new("Maple Street Bakery",
            [new EvidenceFact("A small bakery with a loyal local following.", "https://example-maple-bakery.test", null),
             new EvidenceFact("The website has not been updated in years and has no contact form.", "https://example-maple-bakery.test", null)],
            "https://example-maple-bakery.test", "Local", "Food",
            Risk: "No hiring or purchasing signal was found.")),
        ("prospect-reputation-mismatch", new("Sterling Home Roofing",
            [new EvidenceFact("Highly rated, trusted roofing company with hundreds of positive reviews and a long-standing reputation.", "https://example-sterling-roofing.test", null),
             new EvidenceFact("The current website is broken on mobile and hasn't been updated in years.", "https://example-sterling-roofing.test", null)],
            "https://example-sterling-roofing.test", "Regional", "HomeServices")),
        ("prospect-excluded-industry", new("Bayview Legal Group",
            [new EvidenceFact("An established law firm with an outdated website and no online intake form.", "https://example-bayview-legal.test", null)],
            "https://example-bayview-legal.test", "Regional", "Legal")),
        ("prospect-thin-evidence", new("Downtown Coffee Cart",
            [new EvidenceFact("A small coffee cart; not much else is known.", null, null)],
            null, null, "Food")),
        ("prospect-no-contact", new("Northgate Landscaping",
            [new EvidenceFact("An established landscaping company with an outdated site.", "https://example-northgate-landscaping.test", null),
             new EvidenceFact("No phone number listed and no way to reach the business was found anywhere online.", "https://example-northgate-landscaping.test", null)],
            "https://example-northgate-landscaping.test", "Local", "HomeServices")),
        ("prospect-entry-project", new("Value Hardware Supply",
            [new EvidenceFact("A long-standing hardware supplier.", "https://example-value-hardware.test", null),
             new EvidenceFact("There is no online store and the contact form is broken.", "https://example-value-hardware.test", null),
             new EvidenceFact("A contact page lists a phone number.", "https://example-value-hardware.test", null)],
            "https://example-value-hardware.test", "Local", "Retail",
            Fit: "The business needs a new website with an online store.")),
        ("prospect-near-duplicate-a", new("Harbor View Physical Therapy",
            [new EvidenceFact("Established physical therapy clinic, well known and trusted locally.", null, null),
             new EvidenceFact("Outdated website with no online booking.", null, null)],
            null, "Local", "Healthcare")),
        ("prospect-near-duplicate-b", new("Harbor View Physical Therapy Clinic",
            [new EvidenceFact("Same clinic found through a different source, established and highly rated.", null, null),
             new EvidenceFact("Website hasn't been updated in years.", null, null)],
            null, "Local", "Healthcare"))
    ];
}
