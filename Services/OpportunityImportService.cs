using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Services;

// Category's allowed values are the factor keys BusinessProspectEvaluators.BuildRequestJson scores
// against, and must be kept in sync with EVIDENCE_CATEGORY_PROPERTY's enum in the UI repo's
// opportunityJsonTemplates.ts and the BusinessProspectEvidenceCategory type in opportunities.ts.
public record EvidenceFact(
    [Required, StringLength(2000, MinimumLength = 1)] string Fact,
    [StringLength(1000)] string? Source,
    DateTime? Date,
    [StringLength(50), AllowedValues(
        null, "painCostSeverity", "painFrequency", "automationFeasibility", "economicLeverage",
        "containedEngagement", "urgency", "hslDeliveryFit", "buyerAccess",
        "businessStrength", "digitalWeakness", "reputationMismatch", "entryProjectStrength", "general")]
    string? Category = null);

public record CompetitionInfo(
    [StringLength(100)] string? Proposals,
    [Range(0, int.MaxValue)] int? Interviewing,
    [Range(0, int.MaxValue)] int? Hires);

public record ConfidenceInfo(
    [Required, StringLength(20, MinimumLength = 1)] string Level,
    [StringLength(500)] string? Reason);

public record ActiveProjectImportRequest(
    [Required, StringLength(200)] string Title,
    [Required, StringLength(4000, MinimumLength = 20)] string Request,
    string SourceType = "ExplicitDemand",
    [StringLength(200)] string? SourceName = null,
    [StringLength(1000)] string? SourceUrl = null,
    DateTime? SourceDate = null,
    [StringLength(200)] string? ExternalId = null,
    [StringLength(100)] string? Budget = null,
    CompetitionInfo? Competition = null,
    [StringLength(1000)] string? Fit = null,
    [StringLength(1000)] string? ProposalAngle = null,
    [StringLength(1000)] string? Risk = null,
    string? OpportunityRating = null,
    ConfidenceInfo? Confidence = null,
    [StringLength(100)] string? ResearchAgent = null);

public record BusinessProspectImportRequest(
    [Required, StringLength(200)] string BusinessName,
    [Required, MinLength(1), MaxLength(30)] IReadOnlyList<EvidenceFact> Evidence,
    [StringLength(1000)] string? WebsiteUrl = null,
    [StringLength(200)] string? Geography = null,
    [StringLength(200)] string? Industry = null,
    [StringLength(200)] string? ExternalId = null,
    string? ProspectType = null,
    [StringLength(1000)] string? Fit = null,
    [StringLength(1000)] string? EntryOffer = null,
    [StringLength(1000)] string? Risk = null,
    string? OpportunityRating = null,
    ConfidenceInfo? Confidence = null,
    [StringLength(100)] string? ResearchAgent = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var total = Evidence?.Sum(x => x.Fact?.Length ?? 0) ?? 0;
        if (total is < 20 or > 30000)
            yield return new ValidationResult("Combined evidence must be between 20 and 30,000 characters.", [nameof(Evidence)]);
    }
}

public record OpportunityImportResult(int Id, bool Created, bool Updated, int? NearDuplicateOfId);

public interface IOpportunityImportService
{
    Task<OpportunityImportResult> ImportActiveProjectAsync(ActiveProjectImportRequest request,
        ActiveProjectSourceType sourceType, bool synthetic, string? syntheticKey, CancellationToken ct);
    Task<OpportunityImportResult> ImportBusinessProspectAsync(BusinessProspectImportRequest request,
        bool synthetic, string? syntheticKey, CancellationToken ct);
}

// Exact-match imports upsert the existing row's source material rather than rejecting it, and never
// touch UserDecision/Notes/DuplicateOfId/IsSynthetic. This is what makes CSV resubmission safe: an
// agent can re-run the same research and re-export a CSV without duplicating rows or clobbering a
// decision a human already made.
public sealed class OpportunityImportService(ApplicationDbContext db) : IOpportunityImportService
{
    public static ResearchConfidence? ParseResearchConfidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<ResearchConfidence>(value.Trim(), true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Research confidence must be Low, Medium, or High.");
    }

    public static PriorityBand? ParseOpportunityRating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<PriorityBand>(value.Trim(), true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Opportunity rating must be Low, Medium, or High.");
    }

    public static BusinessProspectType? ParseProspectType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Regex.Replace(value.Trim(), @"[\s_-]+", "");
        return Enum.TryParse<BusinessProspectType>(normalized, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Prospect type must be OperationalPain, DigitalPresence, Hybrid, or Unknown.");
    }

    public async Task<OpportunityImportResult> ImportActiveProjectAsync(ActiveProjectImportRequest request,
        ActiveProjectSourceType sourceType, bool synthetic, string? syntheticKey, CancellationToken ct)
    {
        var description = OpportunityRadarEngine.ComposeActiveProjectDescription(request);
        var passages = OpportunityRadarEngine.BuildActiveProjectPassages(request.Request, request.Budget, request.Competition);
        var fingerprint = OpportunityRadarEngine.Fingerprint(request.Title, description, request.SourceUrl);
        var researchConfidence = ParseResearchConfidence(request.Confidence?.Level);
        var opportunityRating = ParseOpportunityRating(request.OpportunityRating);
        var trimmedExternalId = request.ExternalId?.Trim();
        Opportunity? existing = null;
        if (!string.IsNullOrEmpty(trimmedExternalId))
            existing = await db.Opportunities.Include(x => x.ActiveProjectDetail).Include(x => x.Evaluations)
                .FirstOrDefaultAsync(x => x.EntityType == OpportunityEntityType.ActiveProject
                    && x.IsSynthetic == synthetic && x.ExternalId == trimmedExternalId, ct);
        existing ??= await db.Opportunities.Include(x => x.ActiveProjectDetail).Include(x => x.Evaluations)
            .FirstOrDefaultAsync(x => x.EntityType == OpportunityEntityType.ActiveProject
                && x.IsSynthetic == synthetic && x.Fingerprint == fingerprint, ct);
        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            existing.Title = request.Title.Trim();
            existing.Description = description;
            existing.SourcePassagesJson = OpportunityRadarEngine.SerializePassages(passages);
            existing.SourceName = request.SourceName?.Trim();
            existing.SourceUrl = request.SourceUrl?.Trim();
            existing.SourceDate = request.SourceDate?.ToUniversalTime();
            existing.ExternalId = trimmedExternalId;
            existing.OpportunityRating = opportunityRating;
            existing.ResearchConfidence = researchConfidence;
            existing.ResearchConfidenceReason = request.Confidence?.Reason?.Trim();
            existing.ResearchAgent = request.ResearchAgent?.Trim();
            existing.Fingerprint = fingerprint;
            existing.UpdatedAt = now;
            existing.ActiveProjectDetail!.Budget = request.Budget?.Trim();
            existing.ActiveProjectDetail!.CompetitionProposals = request.Competition?.Proposals?.Trim();
            existing.ActiveProjectDetail!.CompetitionInterviewing = request.Competition?.Interviewing;
            existing.ActiveProjectDetail!.CompetitionHires = request.Competition?.Hires;
            existing.ActiveProjectDetail!.Fit = request.Fit?.Trim();
            existing.ActiveProjectDetail!.ProposalAngle = request.ProposalAngle?.Trim();
            existing.ActiveProjectDetail!.Risk = request.Risk?.Trim();
            AddStaleEvaluationIfReady(existing);
            await db.SaveChangesAsync(ct);
            return new OpportunityImportResult(existing.Id, false, true, existing.DuplicateOfId);
        }

        var candidates = await db.Opportunities.AsNoTracking().Where(x => x.EntityType == OpportunityEntityType.ActiveProject)
            .Select(x => new { x.Id, x.Title, x.Description }).ToListAsync(ct);
        var near = candidates.Select(x => new { x.Id, Similarity = OpportunityRadarEngine.Similarity($"{request.Title} {description}", $"{x.Title} {x.Description}") })
            .Where(x => x.Similarity >= OpportunityRadarThresholds.ActiveProjectNearDuplicateSimilarity).OrderByDescending(x => x.Similarity).FirstOrDefault();

        var opportunity = new Opportunity
        {
            EntityType = OpportunityEntityType.ActiveProject,
            Title = request.Title.Trim(), Description = description,
            SourceName = request.SourceName?.Trim(), SourceUrl = request.SourceUrl?.Trim(), SourceDate = request.SourceDate?.ToUniversalTime(),
            ExternalId = trimmedExternalId, SourcePassagesJson = OpportunityRadarEngine.SerializePassages(passages),
            OpportunityRating = opportunityRating, ResearchConfidence = researchConfidence, ResearchConfidenceReason = request.Confidence?.Reason?.Trim(), ResearchAgent = request.ResearchAgent?.Trim(),
            Fingerprint = fingerprint, DuplicateOfId = near?.Id, IsSynthetic = synthetic, SyntheticKey = syntheticKey,
            CreatedAt = now, UpdatedAt = now,
            ActiveProjectDetail = new ActiveProjectDetail
            {
                DeclaredSourceType = sourceType,
                Budget = request.Budget?.Trim(), CompetitionProposals = request.Competition?.Proposals?.Trim(),
                CompetitionInterviewing = request.Competition?.Interviewing, CompetitionHires = request.Competition?.Hires,
                Fit = request.Fit?.Trim(), ProposalAngle = request.ProposalAngle?.Trim(), Risk = request.Risk?.Trim()
            }
        };
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync(ct);
        return new OpportunityImportResult(opportunity.Id, true, false, near?.Id);
    }

    public async Task<OpportunityImportResult> ImportBusinessProspectAsync(BusinessProspectImportRequest request,
        bool synthetic, string? syntheticKey, CancellationToken ct)
    {
        var description = OpportunityRadarEngine.ComposeBusinessProspectDescription(request);
        var passages = OpportunityRadarEngine.BuildBusinessProspectPassages(request.Evidence);
        var derivedSourceDate = request.Evidence.Where(x => x.Date.HasValue).Select(x => x.Date!.Value.ToUniversalTime()).DefaultIfEmpty(DateTime.MinValue).Max();
        var normalizedName = OpportunityRadarEngine.NormalizeBusinessName(request.BusinessName);
        var prospectType = ParseProspectType(request.ProspectType);
        var researchConfidence = ParseResearchConfidence(request.Confidence?.Level);
        var opportunityRating = ParseOpportunityRating(request.OpportunityRating);
        var normalizedDomain = OpportunityRadarEngine.NormalizeWebsiteDomain(request.WebsiteUrl);
        var trimmedExternalId = request.ExternalId?.Trim();
        var now = DateTime.UtcNow;

        Opportunity? existing = null;
        if (!string.IsNullOrEmpty(trimmedExternalId))
            existing = await db.Opportunities.Include(x => x.BusinessProspectDetail).Include(x => x.Evaluations)
                .FirstOrDefaultAsync(x => x.EntityType == OpportunityEntityType.BusinessProspect
                    && x.IsSynthetic == synthetic && x.ExternalId == trimmedExternalId, ct);
        if (existing is null && normalizedDomain.Length > 0)
            existing = await db.Opportunities.Include(x => x.BusinessProspectDetail).Include(x => x.Evaluations)
                .FirstOrDefaultAsync(x => x.EntityType == OpportunityEntityType.BusinessProspect
                    && x.IsSynthetic == synthetic && x.BusinessProspectDetail!.NormalizedWebsiteDomain == normalizedDomain, ct);
        if (existing is null && normalizedDomain.Length == 0)
            existing = await db.Opportunities.Include(x => x.BusinessProspectDetail).Include(x => x.Evaluations)
                .FirstOrDefaultAsync(x => x.EntityType == OpportunityEntityType.BusinessProspect
                    && x.IsSynthetic == synthetic && x.BusinessProspectDetail!.NormalizedBusinessName == normalizedName, ct);

        if (existing is not null)
        {
            existing.Title = request.BusinessName.Trim();
            existing.Description = description;
            existing.SourcePassagesJson = OpportunityRadarEngine.SerializePassages(passages);
            existing.SourceDate = derivedSourceDate == DateTime.MinValue ? null : derivedSourceDate;
            existing.ExternalId = trimmedExternalId;
            existing.OpportunityRating = opportunityRating;
            existing.ResearchConfidence = researchConfidence;
            existing.ResearchConfidenceReason = request.Confidence?.Reason?.Trim();
            existing.ResearchAgent = request.ResearchAgent?.Trim();
            existing.UpdatedAt = now;
            existing.BusinessProspectDetail!.WebsiteUrl = request.WebsiteUrl?.Trim();
            existing.BusinessProspectDetail!.NormalizedWebsiteDomain = normalizedDomain;
            existing.BusinessProspectDetail!.Geography = request.Geography?.Trim();
            existing.BusinessProspectDetail!.Industry = request.Industry?.Trim();
            // A reimport that omits prospect_type must not clobber a classification a prior import
            // already recorded - only a freshly supplied type overwrites the stored one, matching the
            // "never touch agent-recorded data" contract this class's own doc comment promises.
            if (prospectType is not null) existing.BusinessProspectDetail!.ImportedProspectType = prospectType;
            existing.BusinessProspectDetail!.NormalizedBusinessName = normalizedName;
            existing.BusinessProspectDetail!.Fit = request.Fit?.Trim();
            existing.BusinessProspectDetail!.EntryOffer = request.EntryOffer?.Trim();
            existing.BusinessProspectDetail!.Risk = request.Risk?.Trim();
            AddStaleEvaluationIfReady(existing);
            await db.SaveChangesAsync(ct);
            return new OpportunityImportResult(existing.Id, false, true, existing.DuplicateOfId);
        }

        var candidates = await db.Opportunities.AsNoTracking().Where(x => x.EntityType == OpportunityEntityType.BusinessProspect)
            .Select(x => new { x.Id, Name = x.BusinessProspectDetail!.NormalizedBusinessName }).ToListAsync(ct);
        var near = candidates.Select(x => new { x.Id, Similarity = OpportunityRadarEngine.Similarity(normalizedName, x.Name) })
            .Where(x => x.Similarity >= OpportunityRadarThresholds.BusinessProspectNearDuplicateSimilarity).OrderByDescending(x => x.Similarity).FirstOrDefault();

        var opportunity = new Opportunity
        {
            EntityType = OpportunityEntityType.BusinessProspect,
            Title = request.BusinessName.Trim(), Description = description,
            SourceDate = derivedSourceDate == DateTime.MinValue ? null : derivedSourceDate,
            ExternalId = trimmedExternalId, SourcePassagesJson = OpportunityRadarEngine.SerializePassages(passages),
            OpportunityRating = opportunityRating, ResearchConfidence = researchConfidence, ResearchConfidenceReason = request.Confidence?.Reason?.Trim(), ResearchAgent = request.ResearchAgent?.Trim(),
            Fingerprint = "", DuplicateOfId = near?.Id, IsSynthetic = synthetic, SyntheticKey = syntheticKey,
            CreatedAt = now, UpdatedAt = now,
            BusinessProspectDetail = new BusinessProspectDetail
            {
                NormalizedBusinessName = normalizedName, WebsiteUrl = request.WebsiteUrl?.Trim(), NormalizedWebsiteDomain = normalizedDomain,
                Geography = request.Geography?.Trim(), Industry = request.Industry?.Trim(), ImportedProspectType = prospectType,
                Fit = request.Fit?.Trim(), EntryOffer = request.EntryOffer?.Trim(), Risk = request.Risk?.Trim()
            }
        };
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync(ct);
        return new OpportunityImportResult(opportunity.Id, true, false, near?.Id);
    }

    // Reimporting a record updates its source material in place (see the class doc comment), but that
    // leaves any prior Ready evaluation describing text that no longer exists. Mark it Stale rather
    // than silently keeping it current - mirrors the capability-change staleness pattern in
    // OpportunityRadarController.UpdatePreferences.
    private static void AddStaleEvaluationIfReady(Opportunity existing)
    {
        var latest = existing.Evaluations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (latest is null || latest.Status != EvaluationStatus.Ready) return;
        existing.Evaluations.Add(new OpportunityEvaluation
        {
            Provider = latest.Provider, Status = EvaluationStatus.Stale, Model = latest.Model,
            QuestionSetVersion = latest.QuestionSetVersion, AssessmentJson = latest.AssessmentJson,
            ProviderResponseJson = latest.ProviderResponseJson, ResultJson = latest.ResultJson,
            Recommendation = latest.Recommendation, PriorityBand = latest.PriorityBand, BudgetStatus = latest.BudgetStatus,
            OpportunityScore = latest.OpportunityScore, JevConfidence = latest.JevConfidence,
            EvaluatedProspectType = latest.EvaluatedProspectType, NeedsVerification = true,
            RubricVersion = latest.RubricVersion, Origin = latest.Origin, SourceEvaluationId = latest.SourceEvaluationId,
            EffectiveWeightsJson = latest.EffectiveWeightsJson,
            Summary = "Source material was re-imported. Reevaluate before relying on this evaluation.",
            NextStep = "Reevaluate.", CreatedAt = DateTime.UtcNow
        });
    }
}
