using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

public partial class OpportunityRadarController
{
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

    private static bool ValidOpportunityRating(string? value)
    {
        try { _ = OpportunityImportService.ParseOpportunityRating(value); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidProspectType(string? value)
    {
        try { _ = OpportunityImportService.ParseProspectType(value); return true; }
        catch (ArgumentException) { return false; }
    }

}
