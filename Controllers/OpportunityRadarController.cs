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
    IOpportunityCsvImportService csvImportService, IOpportunityImportService importService, IConfiguration configuration) : ControllerBase
{
    public record EvaluateRequest(int[]? OpportunityIds, string Provider = "Simulated", string? ConfirmationCode = null);
    public record EvaluationPreviewRequest(int[]? OpportunityIds, string Provider = "Jev");
    public record ReviewRequest(string? Decision, [StringLength(5000)] string Notes);
    public record ActiveProjectPreferencesRequest(string[] Capabilities, string[] PreferredProjectTypes, string[] ExcludedProjectTypes,
        decimal MinimumBudget, int MinimumWeeks, int MaximumWeeks, string IncompleteInformationTolerance, Dictionary<string, int>? Weights);
    public record BusinessProspectPreferencesRequest(string[] PreferredIndustries, string[] ExcludedIndustries,
        string[] PreferredGeographies, string[] ExcludedGeographies, Dictionary<string, int>? Weights);
    public record PreferencesRequest(ActiveProjectPreferencesRequest ActiveProject, BusinessProspectPreferencesRequest BusinessProspect,
        int DigestActiveProjectCount = 3, int DigestBusinessProspectCount = 2);

    [HttpGet]
    public async Task<IActionResult> List(string entityType = "All", string recommendation = "All", string sourceType = "All",
        string decision = "All", string? query = null, int page = 1, CancellationToken ct = default)
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
                Latest = x.Evaluations.OrderByDescending(e => e.CreatedAt)
                    .Select(e => new { e.Recommendation, e.PriorityBand, e.BudgetStatus, e.Summary, e.Status, e.Provider })
                    .FirstOrDefault()
            });

        if (recommendationFilter is not null)
            projected = projected.Where(x => x.Latest != null && x.Latest.Recommendation == recommendationFilter);
        if (sourceTypeFilter is not null)
            projected = projected.Where(x => x.SourceType == sourceTypeFilter);
        if (wantsUnreviewed)
            projected = projected.Where(x => x.ActiveDecision == null && x.ProspectDecision == null);
        else if (activeDecisionFilter is not null || prospectDecisionFilter is not null)
            projected = projected.Where(x => (activeDecisionFilter != null && x.ActiveDecision == activeDecisionFilter)
                || (prospectDecisionFilter != null && x.ProspectDecision == prospectDecisionFilter));
        if (!string.IsNullOrWhiteSpace(query))
            projected = projected.Where(x => EF.Functions.ILike(x.Title, $"%{query}%") || EF.Functions.ILike(x.Description, $"%{query}%"));

        var total = await projected.CountAsync(ct);
        var pageItems = await projected
            .OrderBy(x => x.Latest == null || x.Latest.PriorityBand == null ? 3
                : x.Latest.PriorityBand == PriorityBand.High ? 0
                : x.Latest.PriorityBand == PriorityBand.Medium ? 1
                : x.Latest.PriorityBand == PriorityBand.Low ? 2 : 3)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = pageItems.Select(x => new
        {
            x.Id, entityType = x.EntityType.ToString(), x.Title,
            preview = x.Description.Length > 180 ? x.Description[..180] + "..." : x.Description,
            sourceType = x.EntityType == OpportunityEntityType.ActiveProject ? x.SourceType?.ToString() : null,
            recommendation = x.Latest?.Recommendation?.ToString(), priorityBand = x.Latest?.PriorityBand?.ToString(),
            budgetStatus = x.EntityType == OpportunityEntityType.ActiveProject ? (x.Latest?.BudgetStatus ?? BudgetStatus.Unknown).ToString() : null,
            summary = x.Latest?.Summary, evaluationStatus = x.Latest?.Status.ToString(), evaluationProvider = x.Latest?.Provider.ToString(),
            userDecision = x.EntityType == OpportunityEntityType.ActiveProject ? x.ActiveDecision?.ToString() : x.ProspectDecision?.ToString(),
            x.DuplicateOfId, x.IsSynthetic, x.CreatedAt,
            industry = x.EntityType == OpportunityEntityType.ActiveProject ? null : x.Industry,
            geography = x.EntityType == OpportunityEntityType.ActiveProject ? null : x.Geography,
            websiteDomain = x.EntityType == OpportunityEntityType.ActiveProject ? null : x.WebsiteDomain
        });
        return Ok(new { items, total, page, pageSize });
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
            passages = JsonSerializer.Deserialize<object>(opportunity.SourcePassagesJson),
            opportunity.DuplicateOfId,
            opportunity.IsSynthetic,
            opportunity.Notes,
            opportunity.CreatedAt,
            activeProject = opportunity.ActiveProjectDetail is null ? null : new
            {
                sourceType = opportunity.ActiveProjectDetail.DeclaredSourceType.ToString(),
                userDecision = opportunity.ActiveProjectDetail.UserDecision?.ToString()
            },
            businessProspect = opportunity.BusinessProspectDetail is null ? null : new
            {
                businessName = opportunity.Title,
                websiteUrl = opportunity.BusinessProspectDetail.WebsiteUrl,
                normalizedWebsiteDomain = opportunity.BusinessProspectDetail.NormalizedWebsiteDomain,
                geography = opportunity.BusinessProspectDetail.Geography,
                industry = opportunity.BusinessProspectDetail.Industry,
                userDecision = opportunity.BusinessProspectDetail.UserDecision?.ToString()
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

    [HttpGet("{id:int}/comparison")]
    public async Task<IActionResult> Comparison(int id, CancellationToken ct)
    {
        var opportunity = await db.Opportunities.AsNoTracking().Include(x => x.Evaluations)
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (opportunity is null) return NotFound();
        var latest = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        object? baseline = null;
        string? baselineUnavailableReason = null;
        if (opportunity.EntityType == OpportunityEntityType.ActiveProject)
        {
            var keyword = OpportunityRadarReporting.KeywordBaseline(opportunity, await GetOrCreatePreferences(ct));
            baseline = new
            {
                keyword.MatchedTerms, keyword.KeywordScore, recommendation = keyword.Recommendation.ToString(),
                budgetStatus = keyword.BudgetStatus.ToString(), keyword.HardRules, keyword.Summary
            };
        }
        else
        {
            baselineUnavailableReason = "Keyword baseline is only computed for Active Projects.";
        }
        return Ok(new
        {
            baseline,
            baselineUnavailableReason,
            semantic = latest is null ? null : new
            {
                provider = latest.Provider.ToString(), status = latest.Status.ToString(), model = latest.Model,
                recommendation = latest.Recommendation?.ToString(), priorityBand = latest.PriorityBand?.ToString(),
                latest.Summary, latest.CreatedAt
            },
            isIllustration = opportunity.IsSynthetic,
            note = opportunity.IsSynthetic
                ? "This synthetic comparison illustrates behavior. It is not a benchmark or accuracy measurement."
                : "The keyword baseline is a literal comparison aid, not a trained model or accuracy benchmark."
        });
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(bool includeSynthetic = false, CancellationToken ct = default)
    {
        var opportunities = await db.Opportunities.AsNoTracking().Include(x => x.Evaluations)
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
        if (!ValidUrl(request.SourceUrl)) return BadRequest(new { message = "Source URL must be an absolute http or https URL." });
        var result = await importService.ImportActiveProjectAsync(request, sourceType, false, null, ct);
        return Ok(new { result.Id, result.Created, result.Updated, result.NearDuplicateOfId });
    }

    [HttpPost("import/active-projects/csv"), Consumes("multipart/form-data"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportActiveProjectCsv(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0 || file.Length > 2_000_000) return BadRequest(new { message = "CSV must be between 1 byte and 2 MB." });
        IReadOnlyList<ActiveProjectImportRequest> rows;
        try
        {
            using var stream = file.OpenReadStream();
            rows = csvImportService.ParseActiveProjects(stream);
        }
        catch (CsvImportException ex) { return BadRequest(new { message = ex.Message }); }
        if (rows.Any(x => x.Title.Length is < 1 or > 200 || x.Description.Length is < 20 or > 30000))
            return BadRequest(new { message = "Each row needs a title and a description between 20 and 30,000 characters." });
        if (rows.Any(x => !ValidUrl(x.SourceUrl) || !TryActiveProjectSourceType(x.SourceType, out _)))
            return BadRequest(new { message = "One or more rows has an invalid source_type or source_url." });

        var imported = new List<object>();
        var updated = new List<object>();
        foreach (var row in rows)
        {
            TryActiveProjectSourceType(row.SourceType, out var sourceType);
            var result = await importService.ImportActiveProjectAsync(row, sourceType, false, null, ct);
            if (result.Updated) updated.Add(new { result.Id, row.Title });
            else imported.Add(new { result.Id, row.Title, result.NearDuplicateOfId });
        }
        return Ok(new { imported, updated });
    }

    [HttpPost("import/business-prospects"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportBusinessProspect(BusinessProspectImportRequest request, CancellationToken ct)
    {
        if (!ValidUrl(request.WebsiteUrl)) return BadRequest(new { message = "Website URL must be an absolute http or https URL." });
        if (!ValidUrl(request.SourceUrl)) return BadRequest(new { message = "Source URL must be an absolute http or https URL." });
        var result = await importService.ImportBusinessProspectAsync(request, false, null, ct);
        return Ok(new { result.Id, result.Created, result.Updated, result.NearDuplicateOfId });
    }

    [HttpPost("import/business-prospects/csv"), Consumes("multipart/form-data"), RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> ImportBusinessProspectCsv(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0 || file.Length > 2_000_000) return BadRequest(new { message = "CSV must be between 1 byte and 2 MB." });
        IReadOnlyList<BusinessProspectImportRequest> rows;
        try
        {
            using var stream = file.OpenReadStream();
            rows = csvImportService.ParseBusinessProspects(stream);
        }
        catch (CsvImportException ex) { return BadRequest(new { message = ex.Message }); }
        if (rows.Any(x => x.BusinessName.Length is < 1 or > 200 || x.Evidence.Length is < 20 or > 30000))
            return BadRequest(new { message = "Each row needs a business name and evidence between 20 and 30,000 characters." });
        if (rows.Any(x => !ValidUrl(x.WebsiteUrl) || !ValidUrl(x.SourceUrl)))
            return BadRequest(new { message = "One or more rows has an invalid website_url or source_url." });

        var imported = new List<object>();
        var updated = new List<object>();
        foreach (var row in rows)
        {
            var result = await importService.ImportBusinessProspectAsync(row, false, null, ct);
            if (result.Updated) updated.Add(new { result.Id, row.BusinessName });
            else imported.Add(new { result.Id, row.BusinessName, result.NearDuplicateOfId });
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
        if (!TryProvider(request.Provider, out var provider)) return BadRequest(new { message = "Invalid evaluation provider." });
        var ids = request.OpportunityIds?.Distinct().ToArray() ?? [];
        if (ids.Length > 100) return BadRequest(new { message = "Evaluation batches are limited to 100 records." });
        var opportunities = await db.Opportunities.AsNoTracking().Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .Where(x => ids.Length == 0 || ids.Contains(x.Id)).OrderBy(x => x.Id).Take(100).ToListAsync(ct);
        if (opportunities.Count == 0) return BadRequest(new { message = "Choose at least one opportunity to evaluate." });
        var preferences = await GetOrCreatePreferences(ct);
        var evaluatorsByType = ResolveEvaluators(provider, opportunities);
        var estimatedTokens = opportunities.Sum(x => evaluatorsByType[x.EntityType].EstimateMaximumInputTokens(x, preferences));
        var inputPrice = configuration.GetValue("OpportunityRadar:JevInputPricePerMillionTokens", 0.042m);
        var estimatedCost = provider == EvaluationProvider.Jev ? estimatedTokens / 1_000_000m * inputPrice : 0m;
        var batchCap = configuration.GetValue("OpportunityRadar:JevMaximumEstimatedBatchCostUsd", 0.05m);
        var dailyLimit = configuration.GetValue("OpportunityRadar:JevDailyInputTokenLimit", 5_000_000);
        var usedToday = await db.OpportunityEvaluations.AsNoTracking()
            .Where(x => x.Provider == EvaluationProvider.Jev && x.CreatedAt >= DateTime.UtcNow.AddHours(-24) && x.InputTokens != null)
            .SumAsync(x => x.InputTokens ?? 0, ct);
        var jevAvailable = evaluators.Where(x => x.Provider == EvaluationProvider.Jev).All(x => x.IsAvailable);
        var evaluatorsAvailable = evaluatorsByType.Values.All(x => x.IsAvailable);
        var allowed = evaluatorsAvailable && estimatedCost <= batchCap && usedToday + estimatedTokens <= dailyLimit;
        var confirmationCode = ConfirmationCode(opportunities, provider, estimatedTokens);
        return Ok(new
        {
            provider = provider.ToString(), recordCount = opportunities.Count, estimatedMaximumInputTokens = estimatedTokens,
            estimatedMaximumCostUsd = decimal.Round(estimatedCost, 6), maximumBatchCostUsd = batchCap,
            rollingDailyInputTokensUsed = usedToday, rollingDailyInputTokenLimit = dailyLimit,
            liveAvailable = jevAvailable, allowed,
            reason = !evaluatorsAvailable ? "Live Jev evaluation is not configured."
                : estimatedCost > batchCap ? "The estimated maximum cost exceeds the server batch ceiling."
                : usedToday + estimatedTokens > dailyLimit ? "The rolling daily input token limit would be exceeded." : null,
            confirmationCode
        });
    }

    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate(EvaluateRequest request, CancellationToken ct)
    {
        if (!TryProvider(request.Provider, out var provider)) return BadRequest(new { message = "Invalid evaluation provider." });
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

        if (provider == EvaluationProvider.Jev)
        {
            var preview = await ValidateLiveBatch(opportunities, evaluatorsByType, preferences, request.ConfirmationCode, ct);
            if (preview is not null) return preview;
        }

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
                var questionSetVersion = provider == EvaluationProvider.Jev
                    ? (item.Opportunity.EntityType == OpportunityEntityType.ActiveProject ? JevActiveProjectEvaluator.QuestionSetVersion : JevBusinessProspectEvaluator.QuestionSetVersion)
                    : "radar-v1";
                item.Opportunity.Evaluations.Add(new OpportunityEvaluation
                {
                    Provider = provider, Status = EvaluationStatus.Failed, Model = provider == EvaluationProvider.Jev ? "jev-latest" : "simulation-v1",
                    QuestionSetVersion = questionSetVersion,
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
            if (!string.IsNullOrWhiteSpace(request.Decision) && !Enum.TryParse<ActiveProjectDecision>(request.Decision, out _))
                return BadRequest(new { message = "Invalid decision." });
            opportunity.ActiveProjectDetail!.UserDecision = string.IsNullOrWhiteSpace(request.Decision) ? null : Enum.Parse<ActiveProjectDecision>(request.Decision);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.Decision) && !Enum.TryParse<BusinessProspectDecision>(request.Decision, out _))
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

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken ct) => Ok(ToPreferencesResponse(await GetOrCreatePreferences(ct)));

    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences(PreferencesRequest request, CancellationToken ct)
    {
        if (request.ActiveProject.Capabilities.Length is < 1 or > 30 || request.ActiveProject.MinimumBudget < 0
            || request.ActiveProject.MinimumWeeks < 1 || request.ActiveProject.MaximumWeeks < request.ActiveProject.MinimumWeeks
            || request.ActiveProject.MaximumWeeks > 104
            || !Enum.TryParse<IncompleteInformationTolerance>(request.ActiveProject.IncompleteInformationTolerance, out _)
            || !ValidActiveProjectWeights(request.ActiveProject.Weights)
            || !ValidBusinessProspectWeights(request.BusinessProspect.Weights)
            || request.DigestActiveProjectCount is < 0 or > 25 || request.DigestBusinessProspectCount is < 0 or > 25)
            return BadRequest(new { message = "Invalid screening preferences." });

        var preferences = await GetOrCreatePreferences(ct);
        var oldActiveProjectPreferences = OpportunityRadarEngine.ReadActiveProjectPreferences(preferences);
        var oldBusinessProspectPreferences = OpportunityRadarEngine.ReadBusinessProspectPreferences(preferences);
        preferences.ActiveProjectPreferencesJson = JsonSerializer.Serialize(new
        {
            capabilities = request.ActiveProject.Capabilities.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
            preferredProjectTypes = request.ActiveProject.PreferredProjectTypes.Distinct(StringComparer.OrdinalIgnoreCase),
            excludedProjectTypes = request.ActiveProject.ExcludedProjectTypes.Distinct(StringComparer.OrdinalIgnoreCase),
            minimumBudget = request.ActiveProject.MinimumBudget, minimumWeeks = request.ActiveProject.MinimumWeeks,
            maximumWeeks = request.ActiveProject.MaximumWeeks, incompleteInformationTolerance = request.ActiveProject.IncompleteInformationTolerance,
            weights = request.ActiveProject.Weights
        });
        var newActiveProjectPreferences = OpportunityRadarEngine.ReadActiveProjectPreferences(preferences);
        var capabilitiesChanged = !SetsEqual(oldActiveProjectPreferences.Capabilities, newActiveProjectPreferences.Capabilities);
        var activeProjectPreferencesChanged = !OpportunityRadarEngine.ActiveProjectPreferencesEqual(oldActiveProjectPreferences, newActiveProjectPreferences);

        preferences.BusinessProspectPreferencesJson = JsonSerializer.Serialize(new
        {
            preferredIndustries = request.BusinessProspect.PreferredIndustries.Distinct(StringComparer.OrdinalIgnoreCase),
            excludedIndustries = request.BusinessProspect.ExcludedIndustries.Distinct(StringComparer.OrdinalIgnoreCase),
            preferredGeographies = request.BusinessProspect.PreferredGeographies.Distinct(StringComparer.OrdinalIgnoreCase),
            excludedGeographies = request.BusinessProspect.ExcludedGeographies.Distinct(StringComparer.OrdinalIgnoreCase),
            weights = request.BusinessProspect.Weights
        });
        var newBusinessProspectPreferences = OpportunityRadarEngine.ReadBusinessProspectPreferences(preferences);
        var businessProspectPreferencesChanged = !OpportunityRadarEngine.BusinessProspectPreferencesEqual(oldBusinessProspectPreferences, newBusinessProspectPreferences);
        preferences.DigestActiveProjectCount = request.DigestActiveProjectCount;
        preferences.DigestBusinessProspectCount = request.DigestBusinessProspectCount;
        preferences.UpdatedAt = DateTime.UtcNow;

        var opportunities = await db.Opportunities.Include(x => x.Evaluations)
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail).ToListAsync(ct);
        foreach (var opportunity in opportunities)
        {
            var latest = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (latest is null)
            {
                AddSimulation(opportunity, preferences);
                continue;
            }
            if (latest.Provider == EvaluationProvider.Simulated)
            {
                var relevantPreferencesChanged = opportunity.EntityType == OpportunityEntityType.ActiveProject
                    ? activeProjectPreferencesChanged : businessProspectPreferencesChanged;
                if (relevantPreferencesChanged) AddSimulation(opportunity, preferences);
                continue;
            }
            if (latest.Status != EvaluationStatus.Ready) continue;

            // Only ActiveProject capability changes can make a live Jev result stale: capabilities feed
            // the ActiveProject Jev prompt (hsl_capabilities). Nothing in the BusinessProspect Jev prompt
            // is preference-derived, so BusinessProspect preference changes always recompose locally.
            if (opportunity.EntityType == OpportunityEntityType.ActiveProject && capabilitiesChanged)
            {
                opportunity.Evaluations.Add(new OpportunityEvaluation
                {
                    Provider = EvaluationProvider.Jev, Status = EvaluationStatus.Stale, Model = latest.Model,
                    QuestionSetVersion = latest.QuestionSetVersion, AssessmentJson = latest.AssessmentJson,
                    ProviderResponseJson = latest.ProviderResponseJson, ResultJson = latest.ResultJson,
                    Summary = "Capability preferences changed. Run Jev again before relying on this evaluation.",
                    NextStep = "Reevaluate with Jev.", BudgetStatus = latest.BudgetStatus, CreatedAt = DateTime.UtcNow
                });
                continue;
            }

            RadarResult? result = null;
            if (opportunity.EntityType == OpportunityEntityType.ActiveProject)
            {
                var assessment = OpportunityRadarEngine.DeserializeActiveProjectAssessment(latest.AssessmentJson);
                if (assessment is not null)
                    result = OpportunityRadarEngine.ComposeActiveProject(opportunity, preferences, assessment,
                        ["This recommendation was rescored locally from stored Jev judgments. Jev was not called again."]);
            }
            else
            {
                var assessment = OpportunityRadarEngine.DeserializeBusinessProspectAssessment(latest.AssessmentJson);
                if (assessment is not null)
                    result = OpportunityRadarEngine.ComposeBusinessProspect(opportunity, preferences, assessment,
                        ["This recommendation was rescored locally from stored Jev judgments. Jev was not called again."]);
            }
            if (result is null) continue;
            opportunity.Evaluations.Add(new OpportunityEvaluation
            {
                Provider = EvaluationProvider.Jev, Status = EvaluationStatus.Ready, Model = latest.Model,
                QuestionSetVersion = latest.QuestionSetVersion, AssessmentJson = latest.AssessmentJson,
                ProviderResponseJson = latest.ProviderResponseJson, ResultJson = OpportunityRadarEngine.Serialize(result),
                Recommendation = result.Recommendation, PriorityBand = result.PriorityBand, BudgetStatus = result.BudgetStatus,
                Summary = result.Summary, NextStep = result.NextStep, CreatedAt = DateTime.UtcNow
            });
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
        var opportunities = await db.Opportunities.AsNoTracking().Include(x => x.Evaluations)
            .Include(x => x.ActiveProjectDetail).Include(x => x.BusinessProspectDetail)
            .Where(x => includeSynthetic || !x.IsSynthetic).ToListAsync(ct);

        var activeProjects = opportunities.Where(x => x.EntityType == OpportunityEntityType.ActiveProject).Select(ToSummary)
            .Where(x => x.Recommendation == OpportunityRecommendation.Pursue.ToString() && x.EvaluationStatus == EvaluationStatus.Ready.ToString() && x.UserDecision is null)
            .OrderBy(x => PriorityOrder(x.PriorityBand)).ThenByDescending(x => x.CreatedAt).Take(preferences.DigestActiveProjectCount).ToList();
        var businessProspects = opportunities.Where(x => x.EntityType == OpportunityEntityType.BusinessProspect).Select(ToSummary)
            .Where(x => x.Recommendation == OpportunityRecommendation.Prioritize.ToString() && x.EvaluationStatus == EvaluationStatus.Ready.ToString() && x.UserDecision is null)
            .OrderBy(x => PriorityOrder(x.PriorityBand)).ThenByDescending(x => x.CreatedAt).Take(preferences.DigestBusinessProspectCount).ToList();

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
                capabilities = new[] { ".NET", "C#", "React", "TypeScript", "SQL", "PostgreSQL", "REST APIs", "AWS" },
                preferredProjectTypes = new[] { "Integration", "Automation", "InternalTool", "Reporting", "Portal", "ExistingSoftware" },
                excludedProjectTypes = new[] { "FullTimeEmployment", "EquityOnly", "Unpaid" },
                minimumBudget = 2500m, minimumWeeks = 2, maximumWeeks = 12, incompleteInformationTolerance = "Medium",
                weights = new { capabilityFit = 35, problemClarity = 30, independentScope = 20, informationSufficiency = 15 }
            }),
            BusinessProspectPreferencesJson = JsonSerializer.Serialize(new
            {
                preferredIndustries = Array.Empty<string>(), excludedIndustries = Array.Empty<string>(),
                preferredGeographies = Array.Empty<string>(), excludedGeographies = Array.Empty<string>(),
                weights = new { businessStrength = 15, digitalPresenceWeakness = 25, reputationMismatch = 15, entryProjectStrength = 20, geography = 5, contactability = 10, evidenceCompleteness = 10 }
            }),
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
        return new
        {
            activeProject = new
            {
                activeProject.Capabilities, activeProject.PreferredProjectTypes, activeProject.ExcludedProjectTypes,
                activeProject.MinimumBudget, activeProject.MinimumWeeks, activeProject.MaximumWeeks,
                incompleteInformationTolerance = activeProject.IncompleteInformationTolerance.ToString(),
                weights = new
                {
                    capabilityFit = activeProject.Weights.CapabilityFit, problemClarity = activeProject.Weights.ProblemClarity,
                    independentScope = activeProject.Weights.IndependentScope, informationSufficiency = activeProject.Weights.InformationSufficiency
                }
            },
            businessProspect = new
            {
                businessProspect.PreferredIndustries, businessProspect.ExcludedIndustries,
                businessProspect.PreferredGeographies, businessProspect.ExcludedGeographies,
                weights = new
                {
                    businessStrength = businessProspect.Weights.BusinessStrength, digitalPresenceWeakness = businessProspect.Weights.DigitalPresenceWeakness,
                    reputationMismatch = businessProspect.Weights.ReputationMismatch, entryProjectStrength = businessProspect.Weights.EntryProjectStrength,
                    geography = businessProspect.Weights.Geography, contactability = businessProspect.Weights.Contactability,
                    evidenceCompleteness = businessProspect.Weights.EvidenceCompleteness
                }
            },
            digestActiveProjectCount = value.DigestActiveProjectCount, digestBusinessProspectCount = value.DigestBusinessProspectCount,
            value.UpdatedAt
        };
    }

    private static void AddSimulation(Opportunity opportunity, RadarPreferences preferences)
    {
        RadarResult result;
        string assessmentJson;
        if (opportunity.EntityType == OpportunityEntityType.ActiveProject)
        {
            var (r, assessment) = OpportunityRadarEngine.EvaluateActiveProject(opportunity, preferences);
            result = r; assessmentJson = OpportunityRadarEngine.Serialize(assessment);
        }
        else
        {
            var (r, assessment) = OpportunityRadarEngine.EvaluateBusinessProspect(opportunity, preferences);
            result = r; assessmentJson = OpportunityRadarEngine.Serialize(assessment);
        }
        opportunity.Evaluations.Add(new OpportunityEvaluation
        {
            Provider = EvaluationProvider.Simulated, Status = EvaluationStatus.Ready, Recommendation = result.Recommendation,
            PriorityBand = result.PriorityBand, BudgetStatus = result.BudgetStatus, AssessmentJson = assessmentJson,
            ResultJson = OpportunityRadarEngine.Serialize(result),
            Summary = result.Summary, NextStep = result.NextStep, CreatedAt = DateTime.UtcNow
        });
        opportunity.UpdatedAt = DateTime.UtcNow;
    }

    private static OpportunityEvaluation ToEvaluation(OpportunityEvaluationOutcome outcome, OpportunityEntityType entityType) => new()
    {
        Provider = outcome.Provider, Status = EvaluationStatus.Ready, Model = outcome.Model,
        QuestionSetVersion = outcome.Provider == EvaluationProvider.Jev
            ? (entityType == OpportunityEntityType.ActiveProject ? JevActiveProjectEvaluator.QuestionSetVersion : JevBusinessProspectEvaluator.QuestionSetVersion)
            : "radar-v1",
        Recommendation = outcome.Result.Recommendation, PriorityBand = outcome.Result.PriorityBand,
        BudgetStatus = outcome.Result.BudgetStatus, AssessmentJson = outcome.AssessmentJson,
        ResultJson = OpportunityRadarEngine.Serialize(outcome.Result), ProviderResponseJson = outcome.ProviderResponseJson,
        Summary = outcome.Result.Summary, NextStep = outcome.Result.NextStep, InputTokens = outcome.InputTokens,
        OutputTokens = outcome.OutputTokens, CreatedAt = DateTime.UtcNow
    };

    private static bool TryProvider(string value, out EvaluationProvider provider) =>
        Enum.TryParse(value, true, out provider) && Enum.IsDefined(provider);

    private async Task<IActionResult?> ValidateLiveBatch(IReadOnlyList<Opportunity> opportunities,
        Dictionary<OpportunityEntityType, IOpportunityEvaluator> evaluatorsByType, RadarPreferences preferences,
        string? suppliedConfirmationCode, CancellationToken ct)
    {
        var estimatedTokens = opportunities.Sum(x => evaluatorsByType[x.EntityType].EstimateMaximumInputTokens(x, preferences));
        var inputPrice = configuration.GetValue("OpportunityRadar:JevInputPricePerMillionTokens", 0.042m);
        var estimatedCost = estimatedTokens / 1_000_000m * inputPrice;
        var batchCap = configuration.GetValue("OpportunityRadar:JevMaximumEstimatedBatchCostUsd", 0.05m);
        if (estimatedCost > batchCap)
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = "The estimated maximum cost exceeds the server batch ceiling." });
        var dailyLimit = configuration.GetValue("OpportunityRadar:JevDailyInputTokenLimit", 5_000_000);
        var usedToday = await db.OpportunityEvaluations.AsNoTracking()
            .Where(x => x.Provider == EvaluationProvider.Jev && x.CreatedAt >= DateTime.UtcNow.AddHours(-24) && x.InputTokens != null)
            .SumAsync(x => x.InputTokens ?? 0, ct);
        if (usedToday + estimatedTokens > dailyLimit)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "The rolling daily Jev input token limit would be exceeded." });
        var expected = ConfirmationCode(opportunities, EvaluationProvider.Jev, estimatedTokens);
        if (string.IsNullOrWhiteSpace(suppliedConfirmationCode) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(suppliedConfirmationCode), Encoding.UTF8.GetBytes(expected)))
            return Conflict(new { message = "Preview this exact batch and confirm its estimated maximum cost before evaluation." });
        return null;
    }

    private static string ConfirmationCode(IEnumerable<Opportunity> opportunities, EvaluationProvider provider, int estimatedTokens)
    {
        var material = string.Join('|', opportunities.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.UpdatedAt.Ticks}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}:{estimatedTokens}:{material}"))).ToLowerInvariant();
    }

    private static bool SetsEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(right.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static bool ValidActiveProjectWeights(Dictionary<string, int>? weights)
    {
        if (weights is null) return true;
        var required = new[] { "capabilityFit", "problemClarity", "independentScope", "informationSufficiency" };
        return weights.Count == required.Length && required.All(weights.ContainsKey)
            && weights.Values.All(x => x is >= 0 and <= 100) && weights.Values.Sum() > 0;
    }

    private static bool ValidBusinessProspectWeights(Dictionary<string, int>? weights)
    {
        if (weights is null) return true;
        var required = new[] { "businessStrength", "digitalPresenceWeakness", "reputationMismatch", "entryProjectStrength", "geography", "contactability", "evidenceCompleteness" };
        return weights.Count == required.Length && required.All(weights.ContainsKey)
            && weights.Values.All(x => x is >= 0 and <= 100) && weights.Values.Sum() > 0;
    }

    private static SummaryRow ToSummary(Opportunity opportunity)
    {
        var evaluation = opportunity.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        var isActiveProject = opportunity.EntityType == OpportunityEntityType.ActiveProject;
        return new SummaryRow(opportunity.Id, opportunity.EntityType.ToString(), opportunity.Title,
            opportunity.Description.Length > 180 ? opportunity.Description[..180] + "..." : opportunity.Description,
            isActiveProject ? opportunity.ActiveProjectDetail?.DeclaredSourceType.ToString() : null,
            evaluation?.Recommendation?.ToString(), evaluation?.PriorityBand?.ToString(),
            isActiveProject ? (evaluation?.BudgetStatus ?? BudgetStatus.Unknown).ToString() : null,
            evaluation?.Summary, evaluation?.Status.ToString(), evaluation?.Provider.ToString(),
            isActiveProject ? opportunity.ActiveProjectDetail?.UserDecision?.ToString() : opportunity.BusinessProspectDetail?.UserDecision?.ToString(),
            opportunity.DuplicateOfId, opportunity.IsSynthetic, opportunity.CreatedAt,
            isActiveProject ? null : opportunity.BusinessProspectDetail?.Industry,
            isActiveProject ? null : opportunity.BusinessProspectDetail?.Geography,
            isActiveProject ? null : opportunity.BusinessProspectDetail?.NormalizedWebsiteDomain);
    }

    private sealed record SummaryRow(int Id, string EntityType, string Title, string Preview, string? SourceType, string? Recommendation,
        string? PriorityBand, string? BudgetStatus, string? Summary, string? EvaluationStatus, string? EvaluationProvider,
        string? UserDecision, int? DuplicateOfId, bool IsSynthetic, DateTime CreatedAt, string? Industry, string? Geography, string? WebsiteDomain);

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

    private static IReadOnlyList<(string Key, ActiveProjectSourceType SourceType, ActiveProjectImportRequest Request)> ActiveProjectSamples() =>
    [
        ("integration-strong", ActiveProjectSourceType.ExplicitDemand, new("Shopify supplier reconciliation", "We need a tool that reconciles Shopify orders with supplier spreadsheets, flags mismatches, and produces a daily exception report. Budget is $8,000 and delivery is expected within 8 weeks.", "ExplicitDemand")),
        ("automation-semantic", ActiveProjectSourceType.ExplicitDemand, new("Remove daily order rekeying", "Our operations coordinator copies new orders from email attachments into three vendor systems every morning. We want the repeated entry removed and failures surfaced for review. Budget is $6,000.", "ExplicitDemand")),
        ("keyword-bad-scope", ActiveProjectSourceType.ExplicitDemand, new("Enterprise React .NET transformation", "Seeking a full-time principal engineer to lead a multi-year enterprise React, .NET, SQL, and AWS transformation with a team of twelve developers. Salary and employee benefits provided.", "ExplicitDemand")),
        ("unknown-budget", ActiveProjectSourceType.ExplicitDemand, new("Customer reporting portal", "Build a secure customer portal where clients can view monthly SQL-backed reports and download approved exports. We have a clear design and would like delivery in 10 weeks.", "ExplicitDemand")),
        ("low-budget", ActiveProjectSourceType.ExplicitDemand, new("Inventory dashboard", "Create a React inventory dashboard connected to our existing API. Fixed budget is $500 and the deadline is two weeks.", "ExplicitDemand")),
        ("full-time-role", ActiveProjectSourceType.ExplicitDemand, new("Senior software engineer", "Full-time employee role for a senior C# and React engineer. This position is 40 hours per week and includes salary, health insurance, and paid leave.", "ExplicitDemand")),
        ("vague-request", ActiveProjectSourceType.ExplicitDemand, new("Need an app", "We need an app to make our business better. Please send a quote.", "ExplicitDemand")),
        ("api-risk", ActiveProjectSourceType.ExplicitDemand, new("Legacy vendor synchronization", "Build an integration that synchronizes customer records with our legacy vendor. API access is pending vendor approval and the undocumented API may not expose update operations. Budget is $9,000.", "ExplicitDemand")),
        ("operational-signal", ActiveProjectSourceType.OperationalSignal, new("Daily spreadsheet order processing", "Synthetic company job description: the operations specialist downloads orders, rekeys them into supplier spreadsheets, and emails exception reports each day.", "OperationalSignal")),
        ("integration-near-duplicate", ActiveProjectSourceType.ExplicitDemand, new("Shopify order and supplier reconciliation", "We need a tool to reconcile Shopify orders against supplier spreadsheets, highlight mismatches, and send a daily exception report. Budget is $8,000 with an 8 week delivery target.", "ExplicitDemand"))
    ];

    private static IReadOnlyList<(string Key, BusinessProspectImportRequest Request)> BusinessProspectSamples() =>
    [
        ("prospect-strong", new("Riverside Family Dental", "Synthetic research: established dental practice, well known locally, 5-star reviews and loyal customers for over fifteen years. The website is outdated, not mobile friendly, and has no online booking system. The practice needs a new website with online booking.", "https://example-riverside-dental.test", "Local", "Healthcare")),
        ("prospect-already-modern", new("Crestline Auto Body", "Synthetic research: well-regarded auto body shop with a modern website, mobile friendly, recently redesigned, with online booking already in place.", "https://example-crestline-autobody.test", "Local", "Automotive")),
        ("prospect-unknown-intent", new("Maple Street Bakery", "Synthetic research: a small bakery with a loyal local following. The website has not been updated in years and has no contact form. No hiring or purchasing signal was found.", "https://example-maple-bakery.test", "Local", "Food")),
        ("prospect-reputation-mismatch", new("Sterling Home Roofing", "Synthetic research: highly rated, trusted roofing company with hundreds of positive reviews and a long-standing reputation, but the current website is broken on mobile and hasn't been updated in years.", "https://example-sterling-roofing.test", "Regional", "HomeServices")),
        ("prospect-excluded-industry", new("Bayview Legal Group", "Synthetic research: an established law firm with an outdated website and no online intake form.", "https://example-bayview-legal.test", "Regional", "Legal")),
        ("prospect-thin-evidence", new("Downtown Coffee Cart", "Synthetic research: a small coffee cart, not much else known.", null, null, "Food")),
        ("prospect-no-contact", new("Northgate Landscaping", "Synthetic research: an established landscaping company with an outdated site. No phone number listed and no way to reach the business was found anywhere online.", "https://example-northgate-landscaping.test", "Local", "HomeServices")),
        ("prospect-entry-project", new("Value Hardware Supply", "Synthetic research: a long-standing hardware supplier. The website needs a new website; there is no online store and the contact form is broken. A contact page lists a phone number.", "https://example-value-hardware.test", "Local", "Retail")),
        ("prospect-near-duplicate-a", new("Harbor View Physical Therapy", "Synthetic research pass one: established physical therapy clinic, well known and trusted locally, outdated website with no online booking.", null, "Local", "Healthcare")),
        ("prospect-near-duplicate-b", new("Harbor View Physical Therapy Clinic", "Synthetic research pass two: same clinic found through a different source, established and highly rated, website hasn't been updated in years.", null, "Local", "Healthcare"))
    ];
}
