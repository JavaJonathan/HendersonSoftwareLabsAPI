using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

public partial class OpportunityRadarController
{
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
            evaluation?.NeedsVerification ?? false,
            opportunity.OpportunityRating?.ToString(), opportunity.ResearchConfidence?.ToString());
    }

    private sealed record SummaryRow(int Id, string EntityType, string Title, string Preview, string? SourceType, string? Recommendation,
        string? PriorityBand, string? BudgetStatus, string? Summary, string? EvaluationStatus, string? EvaluationProvider,
        string? UserDecision, int? DuplicateOfId, bool IsSynthetic, DateTime CreatedAt, string? Industry, string? Geography, string? WebsiteDomain,
        decimal? OpportunityScore, decimal? JevConfidence, string? ProspectType, bool NeedsVerification,
        string? OpportunityRating, string? ResearchConfidence);

    private static int PriorityOrder(string? value) => value switch { "High" => 0, "Medium" => 1, "Low" => 2, _ => 3 };
}
