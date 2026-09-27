namespace HendersonSoftwareLabsAPI.Services;

/// <summary>
/// Tuning knobs for Opportunity Radar's dedup and scoring pipeline, gathered here (mirroring
/// <see cref="LineGameRules"/>'s pattern) so a rubric revision doesn't require hunting through
/// OpportunityImportService, the Jev evaluators, and OpportunityRadarV2 for every threshold that
/// needs to move together.
/// </summary>
public static class OpportunityRadarThresholds
{
    /// <summary>Fingerprint/description similarity (0-1) above which an Active Project import is flagged as a near-duplicate for review.</summary>
    public const double ActiveProjectNearDuplicateSimilarity = 0.62;

    /// <summary>Business-name similarity (0-1) above which a Business Prospect import is flagged as a near-duplicate for review.</summary>
    public const double BusinessProspectNearDuplicateSimilarity = 0.72;

    /// <summary>A Noul (0-1 "how true is this") answer at or above this counts as "yes" for a deterministic boolean check.</summary>
    public const double NoulYesThreshold = 0.67;

    /// <summary>Any single Jev confidence value (a classification, or one factor's confidence) below this triggers a "needs review" flag.</summary>
    public const double ConfidenceReviewFloor = 0.55;

    /// <summary>Below this, the evaluation's overall blended Jev confidence is flagged as needing review (distinct from the per-value floor above).</summary>
    public const decimal OverallConfidenceFloor = 0.65m;

    /// <summary>Opportunity score (0-100) at or above which priority is High.</summary>
    public const decimal HighPriorityScore = 75m;

    /// <summary>Opportunity score (0-100) at or above which priority is Medium; below this it's Low.</summary>
    public const decimal MediumPriorityScore = 55m;
}
