using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

public partial class OpportunityRadarController
{
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
                x.OpportunityRating, x.ResearchConfidence,
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

        // A second, PK-bounded query for at most 25 rows on this page, kept separate from `projected`
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
            opportunityRating = x.OpportunityRating?.ToString(), researchConfidence = x.ResearchConfidence?.ToString(),
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
}
