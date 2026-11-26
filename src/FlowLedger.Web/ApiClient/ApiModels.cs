namespace FlowLedger.Web.ApiClient;

// ── Transaction ───────────────────────────────────────────────────────────────

// ── RecurringFlow ─────────────────────────────────────────────────────────────

// ── Forecast (flattened API surface) ──────────────────────────────────────────

public sealed record SyncStatusDto(DateTimeOffset? LastSyncedAt, int MinIntervalMinutes, string Provider);

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
    public List<string> Warnings { get; init; } = [];
    public List<ForecastBandPointDto> Bands { get; init; } = [];
    public decimal TypicalWeeklySpend { get; init; }
    public decimal WeeklySpendStdDev { get; init; }
    public int SpendHistoryWeeks { get; init; }
}

public sealed class ForecastBandPointDto
{
    public DateOnly Date { get; init; }
    public decimal Expected { get; init; }
    public decimal Low { get; init; }
    public decimal High { get; init; }
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

public sealed record AskQueryAnswer(string Question, string? Query, string? Error, int Attempts, string? Note = null);

public sealed record CategorizationResultDto(int Categorized, int ByLearnedMerchant, int ByKeyword, int Transfers, int StillUncategorized, int ByModel = 0, int Suggested = 0, int ByLlm = 0);

public sealed record CategorySpendDto(string Category, decimal Amount);

public sealed record MonthCashFlowDto(string Month, decimal Income, decimal Spending);

public sealed record NetWorthPointDto(string Date, decimal Value);

public sealed record DashboardInsightsDto(
    List<CategorySpendDto> SpendingByCategory, List<MonthCashFlowDto> CashFlow, List<NetWorthPointDto> NetWorth,
List<MerchantSpendDto>? TopMerchants = null, List<CategoryMoverDto>? Movers = null, string? PreviousLabel = null);
public sealed record MerchantSpendDto(string Merchant, decimal Amount, int Visits);
public sealed record CategoryMoverDto(string Category, decimal Current, decimal Previous, decimal Change);

public sealed record BudgetStatusDto(string Category, decimal MonthlyLimit, decimal Spent, decimal Remaining, double PercentUsed, bool Over);

public sealed record BudgetsDto(string Month, List<BudgetStatusDto> Budgets, Dictionary<string, decimal> SpendByCategory);

public sealed record GoalDto(Guid Id, string Name, decimal TargetAmount, decimal SavedAmount, decimal Remaining, double PercentComplete, DateOnly? TargetDate, decimal? MonthlyNeeded, bool Complete);

public sealed record DebtRowDto(string Name, decimal Balance, decimal AnnualRatePercent, decimal MinimumPayment);

public sealed record DebtPayoffDto(string Name, int PayoffMonth, decimal InterestPaid);

public sealed record PayoffPlanDto(string Strategy, bool Feasible, int Months, decimal TotalInterest, decimal TotalPaid, List<DebtPayoffDto> Debts);

public sealed record DebtPlanDto(PayoffPlanDto Avalanche, PayoffPlanDto Snowball);

public sealed record AlertDto(string Severity, string Title, string Detail);

public sealed record DuplicateScanDto(int Total, int AutoMergeable, List<DuplicateSuggestionDto> Suggestions);

public sealed record AutoMergeDto(int Merged, int Suggested);
public sealed record CategoryMergeDto(Guid SourceId, string OldPath, Guid TargetId, string TargetPath);
public sealed record CategoryMoveDto(Guid Id, string OldPath, string NewPath, string DisplayName, Guid? ParentId);
public sealed record CategoryMoveResultDto(int Moved, int Merged, int TransactionsAffected, List<CategoryMergeDto> Merges, List<CategoryMoveDto> Moves);

public sealed record PeriodPerformanceDto(string Period, int Days, decimal StartBalance, decimal NetContributions, decimal Gain, double? GainPercent);

public sealed record AccountPerformanceDto(Guid AccountId, string Name, decimal Balance, List<PeriodPerformanceDto> Periods);

public sealed record EnvironmentInfo(string Environment, bool IsProduction);

// ── Assets ────────────────────────────────────────────────────────────────────

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

public sealed record AutoAddResultDto(int Added, List<string> Names);

public sealed record ProviderInfoDto(string Provider, bool UsesWidget, string? ManageUrl);

/// <summary>Whether the in-app Quiltt Connector is available on this instance.</summary>
public sealed record QuilttConfigDto(bool Enabled, string? ConnectorId = null, string? ScriptUrl = null);

/// <summary>Short-lived, profile-scoped Quiltt session for the browser Connector.</summary>
public sealed record QuilttSessionDto(string ConnectorId, string ScriptUrl, string Token, string ProfileId, DateTimeOffset? ExpiresAt);

/// <summary>Outcome of one Connector run (status: success, cancelled or error).</summary>
public sealed record QuilttConnectResult(string Status, string? ConnectionId);

// ── Financial data connectors (read-only overview) ───────────────────────────

public sealed record ConnectorUsageDto(long CallsToday, long CallsThisMonth, int? DailyBudget, int? MonthlyBudget, bool Paused);

/// <summary>
/// Connector-neutral health of one connection (ADR 0018). <c>Kind</c> is a ProviderIssueKind name (string, so a newer API
/// never breaks an older UI). <c>ProviderCode</c> is the provider's own diagnostic status; <c>RepairRef</c> is an opaque reference handed back to that provider's repair flow (for Quiltt, the connection id).
/// </summary>
public sealed record ConnectionIssueDto(
    string Kind, string? Message = null, bool Retryable = false, bool RequiresUserAction = false,
    DateTimeOffset? RetryAfter = null, DateTimeOffset? At = null, int ConsecutiveFailures = 0, string? ProviderCode = null, string? RepairRef = null);

/// <summary>One of the signed-in tenant's own provider connections (works for non-owner users, unlike the connectors overview).</summary>
public sealed record MyConnectionDto(string Provider, string? Institution = null, string? Status = null, ConnectionIssueDto? Issue = null);

public sealed record ConnectorLinkDto(
    string Institution, string Status, DateTimeOffset LinkedAt, DateTimeOffset UpdatedAt, ConnectionIssueDto? Issue = null);

public sealed record ConnectorAccountDto(Guid AccountId, string Name, string ProviderAccountId);

public sealed record UnassignedAccountDto(Guid AccountId, string Name, bool PinnedManual);

/// <param name="Active">Enabled on this instance (the property keeps its original name for compatibility).</param>
/// <param name="Primary">The connector older single-provider call sites use.</param>
public sealed record ConnectorDto(
    string Id, string Name, string Kind, string Description, bool Active, bool Primary, bool EnabledInConfig, bool CredentialsConfigured,
    List<string> ConfigKeys, List<string>? Capabilities, List<ConnectorAccountDto> Accounts, DateTimeOffset? LastSyncedAt,
    ConnectorUsageDto Usage, List<ConnectorLinkDto> Connections);

public sealed record ConnectorsOverviewDto(
    string ActiveProvider, string SelectionMode, string SelectionNote, List<ConnectorDto> Connectors, List<UnassignedAccountDto> UnassignedAccounts);

public sealed record AccountConnectorLinkDto(Guid AccountId, string ConnectorId, string ProviderAccountId);

public sealed record AvailableProviderAccountDto(string ProviderAccountId, string Name, string AccountType, decimal Balance, string Currency, DateTimeOffset LastSeenUtc);

public sealed record SetAccountConnectorRequest(string ConnectorId, string? ProviderAccountId);

// ── Spending insights (Mint-style breakdown) ─────────────────────────────────

public sealed record BreakdownGroupDto(string Key, string Label, decimal Amount, int Count, decimal Percent, bool HasChildren);
public sealed record TrendPointDto(string Bucket, decimal Amount);
public sealed record BreakdownTransactionDto(
    Guid Id, DateOnly Date, string Description, string? Merchant, string Category, string Account, decimal Amount, List<string> Tags,
    double? Confidence = null, string? SuggestedCategory = null, double? SuggestedConfidence = null);

public sealed record BreakdownResultDto(
    DateOnly From, DateOnly To, bool Income, string GroupBy, string? Parent,
    decimal Total, decimal PreviousTotal, int TransactionCount,
    List<BreakdownGroupDto> Groups, List<TrendPointDto> Trend, List<BreakdownTransactionDto> Transactions,
    int TransfersExcluded, decimal NonSpendingExcluded = 0);

public sealed record HomeAssistantSettingsDto(
    bool Enabled, bool HasWebhook, string? WebhookHost, bool ShareAmounts, bool IncludeNames, int LeadDays,
    List<string> Kinds, DateTimeOffset? LastSentUtc, string? LastError);

public sealed record HomeAssistantStateDto(HomeAssistantSettingsDto Settings, List<string> Kinds, bool CanStoreWebhook);

public sealed record HomeAssistantSaveRequest(
    bool Enabled, string? WebhookUrl, bool ClearWebhook, bool ShareAmounts, bool IncludeNames, int LeadDays, List<string> Kinds);

// ── Integrations (REST / SOAP / webhooks) ───────────────────────────────────

public sealed record IntegrationConfigDto(
    string Url, string Auth, string? AuthUser, Dictionary<string, string> Headers, List<string> Events, string? SoapEndpoint);

public sealed record IntegrationDto(
    Guid Id, string Name, string Kind, bool Enabled, IntegrationConfigDto Config, bool HasCredential, bool HasSigningSecret,
    DateTimeOffset CreatedUtc, DateTimeOffset? LastUsedUtc, string? LastError);

public sealed record IntegrationsStateDto(List<IntegrationDto> Integrations, bool CanStoreSecrets, List<string> Events, int Limit);

public sealed record IntegrationSaveDto(
    string Name, string Kind, bool Enabled, string Url, string Auth, string? AuthUser, string? Credential, bool ClearCredential,
    Dictionary<string, string>? Headers, List<string>? Events, string? SoapEndpoint);

/// <summary><c>SigningSecret</c> is set only when it was just issued (webhook created or rotated); it is never retrievable again.</summary>
public sealed record IntegrationSavedDto(IntegrationDto Integration, string? SigningSecret);

public sealed record IntegrationDeliveryDto(
    Guid Id, Guid IntegrationId, DateTimeOffset AtUtc, string Trigger, string Method, string Target, int? StatusCode, int DurationMs, string? Error);

public sealed record IntegrationCallResultDto(int? StatusCode, string? ContentType, string Body, bool Truncated, int DurationMs, string? Error);

public sealed record RestCallDto(string Method, string? Path, string? Query, Dictionary<string, string>? Headers, string? Body, string? ContentType);

public sealed record SoapInvokeDto(string Operation, string BodyXml, string? Version);

public sealed record WsdlOperationDto(string Name, string SoapAction, string SoapVersion, string Endpoint, string Namespace, string BodySkeleton);

public sealed record SoapOperationsDto(List<WsdlOperationDto> Operations, string? Error);

// ── Local AI (in-process embeddings) ─────────────────────────────────────────

public sealed record LocalAiModelDto(string Id, string Kind, string Title, string Description, long SizeBytes, bool Installed, bool Selected, string License);

public sealed record LocalAiDownloadDto(string ModelId, string State, long ReceivedBytes, long TotalBytes, string? Error);

public sealed record LocalAiStatusDto(
    string Mode, string ActiveEmbedder, int Dimensions, bool Semantic, bool EmbeddingModelInstalled, string? EmbedderNote,
    string EmbeddingModelId, bool LlmEnabled, bool LlmModelInstalled, bool LlmLoaded, string? LlmNote,
    string ModelDirectory, bool DownloadsAllowed, List<LocalAiModelDto> Models, LocalAiDownloadDto? Download);

public sealed record MerchantGroupsDto(string Embedder, bool Semantic, double Threshold, List<MerchantGroupDto> Groups);

public sealed record EvalPointDto(double Threshold, int Predicted, int Correct, double Coverage, double Precision);
public sealed record EvalMethodDto(string Name, double Accuracy, List<EvalPointDto> Curve);
public sealed record EvaluationDto(string Embedder, bool Semantic, int Merchants, int Labels, int Folds, long Milliseconds, string Note, List<EvalMethodDto> Methods);

public sealed record ImportFormatDto(string Id, string Name);

public sealed record AutoImportResultDto(string FormatId, string FormatName, List<string> Warnings, ImportSummaryDto Summary);

/// <summary>One page of transactions and the total matching the filters, for server-side paging.</summary>
public sealed record TransactionPage(List<FlowLedger.Contracts.TransactionDto> Items, int? Total);
