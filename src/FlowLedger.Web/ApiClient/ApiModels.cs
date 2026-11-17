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
    DateTimeOffset CreatedAt,
    string? SuggestedName = null,
    List<string>? Tags = null,
    List<TransactionSplitDto>? Splits = null);

public sealed record TransactionSplitDto(decimal Amount, Guid? CategoryId, string? Notes);

public sealed record FlaggedTransactionDto(TransactionDto Transaction, List<string> Reasons);

public sealed record DashboardActivityDto(
    List<TransactionDto> RecentPosted,
    List<TransactionDto> Pending,
    decimal PendingNetAmount,
    List<TransactionDto> Large,
    decimal LargeThreshold,
    List<FlaggedTransactionDto> Suspicious,
    decimal MoneyInLast30Days,
    decimal MoneyOutLast30Days,
    int TransfersExcluded = 0);

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
    DateTimeOffset CreatedAt,
    decimal MonthlyAmount = 0m);

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
public sealed record BackupRunDto(
    bool Ok, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string? SnapshotDir, long Rows,
    bool? OffsiteOk, string? OffsiteDetail, bool? DrillOk, string? Error);

public sealed record BackupStatusDto(
    bool Healthy, bool Stale, bool OffsiteConfigured, DateTimeOffset? LastSuccessUtc,
    BackupRunDto? LastRun, List<BackupRunDto> Runs);

public sealed record EndpointUsageDto(string Method, string Path, long Calls);

public sealed record ProviderUsageDto(
    string Provider, long CallsToday, long CallsThisMonth, int DailyBudget, int MonthlyBudget, bool Paused,
    List<EndpointUsageDto> ByEndpoint);

public sealed record DataStatusDto(string Environment, BackupStatusDto Backup, ProviderUsageDto ProviderUsage);

public sealed record CategorizationResultDto(int Categorized, int ByLearnedMerchant, int ByKeyword, int Transfers, int StillUncategorized);

public sealed record CategorySpendDto(string Category, decimal Amount);

public sealed record MonthCashFlowDto(string Month, decimal Income, decimal Spending);

public sealed record NetWorthPointDto(string Date, decimal Value);

public sealed record DashboardInsightsDto(
    List<CategorySpendDto> SpendingByCategory, List<MonthCashFlowDto> CashFlow, List<NetWorthPointDto> NetWorth);

public sealed record BudgetStatusDto(string Category, decimal MonthlyLimit, decimal Spent, decimal Remaining, double PercentUsed, bool Over);

public sealed record BudgetsDto(string Month, List<BudgetStatusDto> Budgets, Dictionary<string, decimal> SpendByCategory);

public sealed record GoalDto(Guid Id, string Name, decimal TargetAmount, decimal SavedAmount, decimal Remaining, double PercentComplete, DateOnly? TargetDate, decimal? MonthlyNeeded, bool Complete);

public sealed record DebtRowDto(string Name, decimal Balance, decimal AnnualRatePercent, decimal MinimumPayment);

public sealed record DebtPayoffDto(string Name, int PayoffMonth, decimal InterestPaid);

public sealed record PayoffPlanDto(string Strategy, bool Feasible, int Months, decimal TotalInterest, decimal TotalPaid, List<DebtPayoffDto> Debts);

public sealed record DebtPlanDto(PayoffPlanDto Avalanche, PayoffPlanDto Snowball);

public sealed record AlertDto(string Severity, string Title, string Detail);

public sealed record CategoryRuleDto(string Pattern, string CategoryPath);

public sealed record PeriodPerformanceDto(string Period, int Days, decimal StartBalance, decimal NetContributions, decimal Gain, double? GainPercent);

public sealed record AccountPerformanceDto(Guid AccountId, string Name, decimal Balance, List<PeriodPerformanceDto> Periods);

public sealed record EnvironmentInfo(string Environment, bool IsProduction);

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

// Query & reports -------------------------------------------------------------

public sealed record QueryColumnDto(string Name, string Type);

public sealed record QueryResultDto(List<QueryColumnDto> Columns, List<List<System.Text.Json.JsonElement>> Rows, bool Truncated, long ElapsedMs);

public sealed record VizSpecDto(string Kind, string? X = null, List<string>? Y = null, string? SeriesBy = null, bool Stacked = false);

public sealed record ReportPanelDto(string Title, string Query, VizSpecDto Viz);

public sealed record SavedReportDto(
    string Id, string Name, string? Description, string Kind, bool IsPreset,
    List<ReportPanelDto> Panels, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record SaveReportRequest(string Name, string? Description, string Kind, List<ReportPanelDto> Panels);

public sealed record PanelResultDto(string Title, string Query, VizSpecDto Viz, QueryResultDto? Result, string? Error);

public sealed record RunReportResultDto(string Id, string Name, string? Description, string Kind, List<PanelResultDto> Panels);

public sealed record DatasetColumnInfoDto(string Name, string Type, string Description);

public sealed record DatasetInfoDto(string Name, string Description, List<DatasetColumnInfoDto> Columns);

public sealed record QuerySchemaDto(List<DatasetInfoDto> Datasets, List<string> Functions, List<string> Aggregates, List<string> Operators);

/// <summary>Outcome of running a query: a result, or a user-readable error with its character offset.</summary>
public sealed record QueryRunOutcome(QueryResultDto? Result, string? Error, int Position);

// Bill sheet (spreadsheet mode) ------------------------------------------------

public sealed record SheetSlotDto(int Slot, DateOnly? Due, bool Paid, decimal? Amount, DateOnly? PaidOn, string? Source);

public sealed record SheetCellDto(bool Paid, decimal? Amount, DateOnly? PaidOn, string Source, int PaidCount = 0, int Expected = 1);

public sealed record SheetRowDto(
    Guid Id, string Name, string? Payer, bool IsDebt, bool IsPerpetual, bool IsOptional, bool IsBusiness, bool AutoPay,
    int? DueDay, decimal Estimated, decimal? Actual, string? Service, string? AccountRefMasked, string? MatchText,
    bool NeedsMatchText, Dictionary<string, SheetCellDto> Cells,
    string Frequency = "Monthly", int? IntervalWeeks = null, DateOnly? AnchorDate = null, int? SecondDueDay = null,
    Dictionary<string, List<SheetSlotDto>>? Slots = null, decimal MonthlyEstimated = 0m);

public sealed record SheetTotalsLine(decimal Total, decimal Optional, decimal Required, decimal Personal, decimal Business);

public sealed record SheetTotalsDto(SheetTotalsLine Estimated, SheetTotalsLine Actual);

public sealed record SheetSectionDto(string Name, List<SheetRowDto> Rows, SheetTotalsDto Totals);

public sealed record SheetMonthSummaryDto(string Month, int PaidCount, int TotalCount, decimal PaidAmount, decimal RemainingEstimated);

public sealed record SheetViewDto(
    List<string> Months, string SelectedMonth, List<SheetSectionDto> Sections, SheetTotalsDto GrandTotals, SheetMonthSummaryDto Summary);

public sealed record SaveBillRequest(
    string Section, string Name, string? Payer, bool IsDebt, bool IsPerpetual, bool IsOptional, bool IsBusiness, bool AutoPay,
    int? DueDay, decimal Estimated, decimal? Actual, string? Service, string? AccountRef, string? MatchText,
    string? Frequency = null, int? IntervalWeeks = null, DateOnly? AnchorDate = null, int? SecondDueDay = null);

public sealed record SetPaymentRequest(bool Paid, decimal? Amount, int Slot = 0);

public sealed record ImportSheetRequest(string Csv, bool Replace);

public sealed record SheetImportResult(int Sections, int Bills, int PaidMarks);

public sealed record SheetRefreshResult(int Marked, int Cleared, int Added = 0);

public sealed record RecurringSuggestionDto(
    string Key,
    Guid AccountId,
    string AccountName,
    string Name,
    string Direction,
    decimal Amount,
    string Currency,
    string AmountModel,
    string Frequency,
    int? DayOfMonth,
    int? SecondDayOfMonth,
    int? IntervalWeeks,
    string? AnchorDayOfWeek,
    DateOnly LastSeen,
    DateOnly NextExpected,
    int Occurrences,
    double Confidence,
    bool WouldAutoAdd);

public sealed record AutoAddResultDto(int Added, List<string> Names);
