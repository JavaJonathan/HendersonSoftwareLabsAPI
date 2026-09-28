using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

public partial class OpportunityRadarController
{
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

}
