namespace FlowLedger.Application.Features.Assets;

/// <summary>One point in an asset's valuation history.</summary>
public sealed record AssetValuationDto(Guid Id, decimal Amount, DateOnly AsOf, string Source, string? Note, DateTimeOffset RecordedAt);

/// <summary>Response DTO for an asset. <see cref="Valuations"/> is only populated on single-asset reads.</summary>
public sealed record AssetDto(
    Guid Id,
    string Name,
    string Category,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Notes,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string Currency,
    decimal CurrentValue,
    DateOnly ValuedOn,
    decimal EstimatedValue,
    string ValuationMethod,
    decimal AnnualRate,
    decimal? SalvageValue,
    int? UsefulLifeYears,
    Guid? LinkedAccountId,
    string? LinkedAccountName,
    decimal? LinkedLiability,
    decimal? Equity,
    decimal? GainLoss,
    IReadOnlyDictionary<string, string> Attributes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AssetValuationDto>? Valuations,
    ValuationSpreadDto? Spread = null);

public sealed record ValuationSpreadDto(decimal Low, decimal High, decimal Median, decimal Average, int SourceCount, int WindowDays);

public sealed record CreateAssetRequest(
    string Name,
    string Category,
    decimal CurrentValue,
    string Currency,
    DateOnly? ValuedOn,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Notes,
    string? ValuationMethod,
    decimal? AnnualRate,
    decimal? SalvageValue,
    int? UsefulLifeYears,
    Guid? LinkedAccountId,
    Dictionary<string, string>? Attributes);

/// <summary>Updates descriptive fields and the valuation model. Value changes go through valuations.</summary>
public sealed record UpdateAssetRequest(
    string Name,
    string Category,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Notes,
    string? ValuationMethod,
    decimal? AnnualRate,
    decimal? SalvageValue,
    int? UsefulLifeYears,
    Guid? LinkedAccountId,
    Dictionary<string, string>? Attributes);

public sealed record RecordValuationRequest(decimal Amount, DateOnly? AsOf, string? Source, string? Note);

public sealed record AssetCategoryTemplateDto(
    string Category,
    string Label,
    string DefaultMethod,
    decimal DefaultAnnualRate,
    int? DefaultUsefulLifeYears,
    IReadOnlyList<string> SuggestedFields);

public sealed record AssetCategoryBreakdownDto(string Category, int Count, decimal Value);

/// <summary>Net worth. Currencies are never summed together: see <see cref="MixedCurrencies"/> and <see cref="ByCurrency"/>.</summary>
public sealed record NetWorthDto(
    decimal AccountAssets,
    decimal AccountLiabilities,
    decimal PhysicalAssets,
    decimal NetWorth,
    decimal CarriedByAccounts,
    string Currency = "USD",
    bool MixedCurrencies = false,
    IReadOnlyList<CurrencyTotalDto>? ByCurrency = null);

/// <summary>Net worth components for one currency.</summary>
public sealed record CurrencyTotalDto(
    string Currency,
    decimal AccountAssets,
    decimal AccountLiabilities,
    decimal PhysicalAssets,
    decimal NetWorth,
    decimal CarriedByAccounts);

public sealed record AssetSummaryDto(
    int AssetCount,
    decimal TotalValue,
    decimal TotalCost,
    decimal UnrealizedGainLoss,
    decimal LinkedLiabilities,
    decimal TotalEquity,
    IReadOnlyList<AssetCategoryBreakdownDto> ByCategory,
    IReadOnlyList<AssetDto> TopAssets,
    NetWorthDto NetWorth);

/// <summary>Outcome of asking a valuation feed to refresh an asset.</summary>
public sealed record AssetRefreshResultDto(bool Updated, string Message, AssetDto? Asset);
