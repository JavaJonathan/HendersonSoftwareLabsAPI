using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Services;

public record ActiveProjectImportRequest(
    [Required, StringLength(200)] string Title,
    [Required, StringLength(30000, MinimumLength = 20)] string Description,
    string SourceType = "ExplicitDemand",
    [StringLength(200)] string? SourceName = null,
    [StringLength(1000)] string? SourceUrl = null,
    DateTime? SourceDate = null,
    [StringLength(200)] string? ExternalId = null,
    string? ResearchConfidence = null,
    [StringLength(500)] string? ResearchConfidenceReason = null,
    [StringLength(100)] string? ResearchAgent = null);

public record BusinessProspectImportRequest(
    [Required, StringLength(200)] string BusinessName,
    [Required, StringLength(30000, MinimumLength = 20)] string Evidence,
    [StringLength(1000)] string? WebsiteUrl = null,
    [StringLength(200)] string? Geography = null,
    [StringLength(200)] string? Industry = null,
    [StringLength(200)] string? SourceName = null,
    [StringLength(1000)] string? SourceUrl = null,
    DateTime? SourceDate = null,
    [StringLength(200)] string? ExternalId = null,
    string? ProspectType = null,
    string? ResearchConfidence = null,
    [StringLength(500)] string? ResearchConfidenceReason = null,
    [StringLength(100)] string? ResearchAgent = null);

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
public sealed partial class OpportunityImportService(ApplicationDbContext db) : IOpportunityImportService
{
    public static ResearchConfidence? ParseResearchConfidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<ResearchConfidence>(value.Trim(), true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Research confidence must be Low, Medium, or High.");
    }

    public static BusinessProspectType? ParseProspectType(string? value, string evidence)
    {
        var candidate = value;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = ProspectTypePrefixRegex().Match(evidence).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var normalized = Regex.Replace(candidate.Trim(), @"[\s_-]+", "");
        return Enum.TryParse<BusinessProspectType>(normalized, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Prospect type must be OperationalPain, DigitalPresence, Hybrid, or Unknown.");
    }

    public async Task<OpportunityImportResult> ImportActiveProjectAsync(ActiveProjectImportRequest request,
        ActiveProjectSourceType sourceType, bool synthetic, string? syntheticKey, CancellationToken ct)
    {
        var fingerprint = OpportunityRadarEngine.Fingerprint(request.Title, request.Description, request.SourceUrl);
        var researchConfidence = ParseResearchConfidence(request.ResearchConfidence);
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
            existing.Description = request.Description.Trim();
            existing.SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.Segment(request.Description));
            existing.SourceName = request.SourceName?.Trim();
            existing.SourceUrl = request.SourceUrl?.Trim();
            existing.SourceDate = request.SourceDate?.ToUniversalTime();
            existing.ExternalId = trimmedExternalId;
            existing.ResearchConfidence = researchConfidence;
            existing.ResearchConfidenceReason = request.ResearchConfidenceReason?.Trim();
            existing.ResearchAgent = request.ResearchAgent?.Trim();
            existing.Fingerprint = OpportunityRadarEngine.Fingerprint(request.Title, request.Description, request.SourceUrl);
            existing.UpdatedAt = now;
            AddStaleEvaluationIfReady(existing);
            await db.SaveChangesAsync(ct);
            return new OpportunityImportResult(existing.Id, false, true, existing.DuplicateOfId);
        }

        var candidates = await db.Opportunities.AsNoTracking().Where(x => x.EntityType == OpportunityEntityType.ActiveProject)
            .Select(x => new { x.Id, x.Title, x.Description }).ToListAsync(ct);
        var near = candidates.Select(x => new { x.Id, Similarity = OpportunityRadarEngine.Similarity($"{request.Title} {request.Description}", $"{x.Title} {x.Description}") })
            .Where(x => x.Similarity >= 0.62).OrderByDescending(x => x.Similarity).FirstOrDefault();

        var opportunity = new Opportunity
        {
            EntityType = OpportunityEntityType.ActiveProject,
            Title = request.Title.Trim(), Description = request.Description.Trim(),
            SourceName = request.SourceName?.Trim(), SourceUrl = request.SourceUrl?.Trim(), SourceDate = request.SourceDate?.ToUniversalTime(),
            ExternalId = trimmedExternalId, SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.Segment(request.Description)),
            ResearchConfidence = researchConfidence, ResearchConfidenceReason = request.ResearchConfidenceReason?.Trim(), ResearchAgent = request.ResearchAgent?.Trim(),
            Fingerprint = fingerprint, DuplicateOfId = near?.Id, IsSynthetic = synthetic, SyntheticKey = syntheticKey,
            CreatedAt = now, UpdatedAt = now,
            ActiveProjectDetail = new ActiveProjectDetail { DeclaredSourceType = sourceType }
        };
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync(ct);
        return new OpportunityImportResult(opportunity.Id, true, false, near?.Id);
    }

    public async Task<OpportunityImportResult> ImportBusinessProspectAsync(BusinessProspectImportRequest request,
        bool synthetic, string? syntheticKey, CancellationToken ct)
    {
        var normalizedName = OpportunityRadarEngine.NormalizeBusinessName(request.BusinessName);
        var prospectType = ParseProspectType(request.ProspectType, request.Evidence);
        var researchConfidence = ParseResearchConfidence(request.ResearchConfidence);
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
            existing.Description = request.Evidence.Trim();
            existing.SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.Segment(request.Evidence));
            existing.SourceName = request.SourceName?.Trim();
            existing.SourceUrl = request.SourceUrl?.Trim();
            existing.SourceDate = request.SourceDate?.ToUniversalTime();
            existing.ExternalId = trimmedExternalId;
            existing.ResearchConfidence = researchConfidence;
            existing.ResearchConfidenceReason = request.ResearchConfidenceReason?.Trim();
            existing.ResearchAgent = request.ResearchAgent?.Trim();
            existing.UpdatedAt = now;
            existing.BusinessProspectDetail!.WebsiteUrl = request.WebsiteUrl?.Trim();
            existing.BusinessProspectDetail!.NormalizedWebsiteDomain = normalizedDomain;
            existing.BusinessProspectDetail!.Geography = request.Geography?.Trim();
            existing.BusinessProspectDetail!.Industry = request.Industry?.Trim();
            // A reimport that omits prospect_type (no explicit value and no "Prospect type: X" line in the
            // new evidence) must not clobber a classification a prior import already recorded - only a
            // freshly supplied type overwrites the stored one, matching the "never touch agent-recorded
            // data" contract this class's own doc comment promises.
            if (prospectType is not null) existing.BusinessProspectDetail!.ImportedProspectType = prospectType;
            existing.BusinessProspectDetail!.NormalizedBusinessName = normalizedName;
            AddStaleEvaluationIfReady(existing);
            await db.SaveChangesAsync(ct);
            return new OpportunityImportResult(existing.Id, false, true, existing.DuplicateOfId);
        }

        var candidates = await db.Opportunities.AsNoTracking().Where(x => x.EntityType == OpportunityEntityType.BusinessProspect)
            .Select(x => new { x.Id, Name = x.BusinessProspectDetail!.NormalizedBusinessName }).ToListAsync(ct);
        var near = candidates.Select(x => new { x.Id, Similarity = OpportunityRadarEngine.Similarity(normalizedName, x.Name) })
            .Where(x => x.Similarity >= 0.72).OrderByDescending(x => x.Similarity).FirstOrDefault();

        var opportunity = new Opportunity
        {
            EntityType = OpportunityEntityType.BusinessProspect,
            Title = request.BusinessName.Trim(), Description = request.Evidence.Trim(),
            SourceName = request.SourceName?.Trim(), SourceUrl = request.SourceUrl?.Trim(), SourceDate = request.SourceDate?.ToUniversalTime(),
            ExternalId = trimmedExternalId, SourcePassagesJson = OpportunityRadarEngine.SerializePassages(OpportunityRadarEngine.Segment(request.Evidence)),
            ResearchConfidence = researchConfidence, ResearchConfidenceReason = request.ResearchConfidenceReason?.Trim(), ResearchAgent = request.ResearchAgent?.Trim(),
            Fingerprint = "", DuplicateOfId = near?.Id, IsSynthetic = synthetic, SyntheticKey = syntheticKey,
            CreatedAt = now, UpdatedAt = now,
            BusinessProspectDetail = new BusinessProspectDetail
            {
                NormalizedBusinessName = normalizedName, WebsiteUrl = request.WebsiteUrl?.Trim(), NormalizedWebsiteDomain = normalizedDomain,
                Geography = request.Geography?.Trim(), Industry = request.Industry?.Trim(), ImportedProspectType = prospectType
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

    [GeneratedRegex(@"(?im)^\s*Prospect\s+type\s*:\s*(Operational\s*Pain|Digital\s*Presence|Hybrid|Unknown)\s*\.?\s*$")]
    private static partial Regex ProspectTypePrefixRegex();
}
