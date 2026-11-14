namespace FlowLedger.Web.ApiClient;

// ── Account ───────────────────────────────────────────────────────────────────

public sealed record AccountDto(
    Guid Id,
    string Name,
    string AccountType,
    decimal BalanceAmount,
    string BalanceCurrency,
    string? Institution,
    string? ExternalAccountRef,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastBalanceConfirmedAt);

public sealed record CreateAccountRequest(
    string Name,
    string AccountType,
    decimal StartingBalance,
    string Currency,
    string? Institution,
    decimal? CreditLimit);

public sealed record UpdateAccountRequest(string Name);

// ── Transaction ───────────────────────────────────────────────────────────────

public sealed record TransactionDto(
    Guid Id,
    Guid AccountId,
    decimal Amount,
    string Currency,
    string Direction,
    string Description,
    string Status,
    string Source,
    DateOnly EffectiveDate,
    DateOnly? PostedDate,
    Guid? CategoryId,
    string? MerchantName,
    string? Notes,
    string? Fingerprint,
    DateTimeOffset CreatedAt);

public sealed record FlaggedTransactionDto(TransactionDto Transaction, List<string> Reasons);

public sealed record DashboardActivityDto(
    List<TransactionDto> RecentPosted,
    List<TransactionDto> Pending,
    decimal PendingNetAmount,
    List<TransactionDto> Large,
    decimal LargeThreshold,
    List<FlaggedTransactionDto> Suspicious,
    decimal MoneyInLast30Days,
    decimal MoneyOutLast30Days);

public sealed record CreateTransactionRequest(
    Guid AccountId,
    decimal Amount,
    string Currency,
    string Direction,
    string Description,
    DateOnly EffectiveDate,
    DateOnly? PostedDate,
    Guid? CategoryId,
    string? MerchantName,
    string? Notes);

// ── Category ──────────────────────────────────────────────────────────────────

public sealed record CategoryDto(
    Guid Id,
    string Path,
    string DisplayName,
    bool IsSystem,
    Guid? ParentId);

// ── RecurringFlow ─────────────────────────────────────────────────────────────

public sealed record RecurringFlowDto(
    Guid Id,
    Guid AccountId,
    string Name,
    decimal Amount,
    string Currency,
    string Direction,
    string AmountModel,
    string RecurrenceFrequency,
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly StartDate,
    DateOnly? EndDate,
    Guid? CategoryId,
    string? Counterparty,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record CreateRecurringFlowRequest(
    Guid AccountId,
    string Name,
    decimal Amount,
    string Currency,
    string Direction,
    string AmountModel,
    string RecurrenceFrequency,
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly StartDate,
    DateOnly? EndDate,
    Guid? CategoryId,
    string? Counterparty);

public sealed record UpdateRecurringFlowRequest(
    decimal Amount,
    string AmountModel);

// ── Forecast (flattened API surface) ──────────────────────────────────────────

public sealed class ForecastResultDto
{
    public Guid ForecastRunId { get; init; }
    public DateOnly AsOf { get; init; }
    public DateOnly HorizonStart { get; init; }
    public DateOnly HorizonEnd { get; init; }
    public List<AccountForecastSeriesDto> AccountSeries { get; init; } = [];
    public List<AggregateForecastPointDto> AggregateSeries { get; init; } = [];
    public List<AccountLowWaterMarkDto> LowWaterMarks { get; init; } = [];
    public AggregateLowWaterMarkDto? AggregateLowWaterMark { get; init; }
    public List<OverdraftWarningDto> OverdraftWarnings { get; init; } = [];
}

public sealed class AccountForecastSeriesDto
{
    public Guid AccountId { get; init; }
    public decimal StartingBalanceAmount { get; init; }
    public string StartingBalanceCurrency { get; init; } = "USD";
    public List<ForecastPointDto> Points { get; init; } = [];
}

public sealed class ForecastPointDto
{
    public DateOnly Date { get; init; }
    public decimal BalanceAmount { get; init; }
    public string BalanceCurrency { get; init; } = "USD";
    public decimal NetChangeAmount { get; init; }
    public List<ForecastLineItemDto> ContributingItems { get; init; } = [];
}

public sealed class ForecastLineItemDto
{
    public string Source { get; init; } = "";
    public Guid SourceId { get; init; }
    public string Label { get; init; } = "";
    public decimal AmountValue { get; init; }
    public string Direction { get; init; } = "";
    public decimal BalanceDeltaAmount { get; init; }
    public bool IsActual { get; init; }
}

public sealed class AggregateForecastPointDto
{
    public DateOnly Date { get; init; }
    public decimal BalanceAmount { get; init; }
}

public sealed class AccountLowWaterMarkDto
{
    public Guid AccountId { get; init; }
    public decimal MinBalanceAmount { get; init; }
    public DateOnly Date { get; init; }
}

public sealed class AggregateLowWaterMarkDto
{
    public decimal MinBalanceAmount { get; init; }
    public DateOnly Date { get; init; }
}

public sealed class OverdraftWarningDto
{
    public Guid AccountId { get; init; }
    public DateOnly FirstBreachDate { get; init; }
    public decimal ProjectedBalanceAmount { get; init; }
}

// ── Sync ──────────────────────────────────────────────────────────────────────

public sealed record ConnectResult(string MemberId, string Provider);

/// <summary>
/// Matches the API's <c>Application.Abstractions.SyncResult</c> shape:
/// <c>accountsUpserted</c>, <c>transactionsAdded</c>, <c>transactionsSkipped</c>, <c>recurringFlowsAdded</c>.
/// </summary>
public sealed record SyncResult(int AccountsUpserted, int TransactionsAdded, int TransactionsSkipped, int RecurringFlowsAdded);

// ── Assets ────────────────────────────────────────────────────────────────────

public sealed record AssetValuationDto(Guid Id, decimal Amount, DateOnly AsOf, string Source, string? Note, DateTimeOffset RecordedAt);

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
    Dictionary<string, string> Attributes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    List<AssetValuationDto>? Valuations);

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
    List<string> SuggestedFields);

public sealed record AssetCategoryBreakdownDto(string Category, int Count, decimal Value);

public sealed record NetWorthDto(
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
    List<AssetCategoryBreakdownDto> ByCategory,
    List<AssetDto> TopAssets,
    NetWorthDto NetWorth);

public sealed record AssetRefreshResultDto(bool Updated, string Message, AssetDto? Asset);
