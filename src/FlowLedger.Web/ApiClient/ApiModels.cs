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
