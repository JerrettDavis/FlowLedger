using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Assets;

/// <summary>
/// Suggested fixed/custom fields and sensible valuation defaults per category. The UI renders these
/// as pre-labelled attribute inputs; users may add any custom field in addition.
/// </summary>
public static class AssetCategoryTemplates
{
    private static readonly AssetCategoryTemplateDto[] All =
    [
        T(AssetCategory.RealEstate, "Real estate", ValuationMethod.Appreciation, 0.03m, null, "Address", "Parcel number", "Square feet", "Year built"),
        T(AssetCategory.Vehicle, "Vehicle", ValuationMethod.DecliningBalance, 0.15m, null, "VIN", "Year", "Mileage", "License plate", "Color"),
        T(AssetCategory.Electronics, "Electronics", ValuationMethod.DecliningBalance, 0.25m, null, "Storage", "Color", "Warranty expires", "IMEI / Service tag"),
        T(AssetCategory.Firearm, "Firearm", ValuationMethod.Manual, 0m, null, "Type", "Caliber", "Action", "Barrel length", "Registration / permit"),
        T(AssetCategory.Jewelry, "Jewelry", ValuationMethod.Appreciation, 0.02m, null, "Material", "Carat / weight", "Appraisal date", "Appraiser"),
        T(AssetCategory.Collectible, "Collectible", ValuationMethod.Appreciation, 0.03m, null, "Edition", "Condition", "Grading", "Provenance"),
        T(AssetCategory.Art, "Art", ValuationMethod.Manual, 0m, null, "Artist", "Medium", "Dimensions", "Provenance"),
        T(AssetCategory.Furniture, "Furniture", ValuationMethod.DecliningBalance, 0.12m, null, "Material", "Room", "Condition"),
        T(AssetCategory.Appliance, "Appliance", ValuationMethod.StraightLine, 0m, 12, "Warranty expires", "Energy rating", "Room"),
        T(AssetCategory.Tool, "Tool / equipment", ValuationMethod.DecliningBalance, 0.10m, null, "Condition", "Warranty expires"),
        T(AssetCategory.Investment, "Investment / precious metal", ValuationMethod.Appreciation, 0.03m, null, "Metal / type", "Weight", "Purity", "Storage location"),
        T(AssetCategory.Other, "Other", ValuationMethod.Manual, 0m, null),
    ];

    public static IReadOnlyList<AssetCategoryTemplateDto> List() => All;

    private static AssetCategoryTemplateDto T(
        AssetCategory category, string label, ValuationMethod method, decimal rate, int? life, params string[] fields)
        => new(category.ToString(), label, method.ToString(), rate, life, fields);
}
