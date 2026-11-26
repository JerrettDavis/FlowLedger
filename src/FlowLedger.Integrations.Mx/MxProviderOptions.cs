namespace FlowLedger.Integrations.Mx;

/// <summary>
/// MX-specific tunables (NOT credentials — those live in
/// <see cref="FlowLedger.Integrations.Abstractions.FinancialProviderOptions"/> under the
/// <c>Mx</c> section, reused as-is). Bind from the <c>Mx:Provider</c> configuration section.
/// </summary>
public sealed class MxProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Mx:Provider";

    /// <summary>
    /// MX <c>records_per_page</c> page size for accounts/transactions. MX allows up to 1000.
    /// </summary>
    public int RecordsPerPage { get; set; } = 100;

    /// <summary>
    /// Cooldown window for manual (user-triggered) refreshes per (tenant, member).
    /// A second manual refresh inside this window is rejected to control aggregation cost.
    /// </summary>
    public TimeSpan ManualRefreshCooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Institution code used when creating a member during a connect flow. In production the
    /// real institution is chosen by the user inside the MX Connect widget; this is the seed
    /// value used to provision the member before the widget hand-off (and by the test fixtures).
    /// </summary>
    public string DefaultInstitutionCode { get; set; } = "mxbank";

    /// <summary>
    /// Optional per-tenant monthly aggregation budget (number of allowed manual refreshes).
    /// Zero or negative means "no budget cap". Reserved for future enforcement.
    /// </summary>
    public int MonthlyManualRefreshBudget { get; set; }

    /// <summary>
    /// Safety cap on the number of pages fetched in a single <c>GetAccountsAsync</c> call.
    /// Prevents an unbounded loop if the MX API returns pathological pagination data.
    /// Defaults to 100, which accommodates up to 100 × <see cref="RecordsPerPage"/> accounts.
    /// </summary>
    public int MaxPages { get; set; } = 100;

    /// <summary>
    /// Days of overlap re-read on every incremental sync (cursor watermark = today - this).
    /// Covers late-posting and pending→posted changes; fingerprint dedupe makes re-reads free of
    /// duplicates. Larger = safer, slightly more API pages per sync. Default 30.
    /// </summary>
    public int IncrementalOverlapDays { get; set; } = 30;

    /// <summary>
    /// When set to an archived MX directory (e.g. <c>~/.flowledger/objects/raw/MX</c>), the provider replays
    /// archived responses instead of calling MX: zero API cost, no network. Unset (default) = live MX.
    /// Config key: <c>Mx:Provider:ReplayArchivePath</c>.
    /// </summary>
    public string? ReplayArchivePath { get; set; }

    /// <summary>Hard cap on MX HTTP calls per UTC day, counted from the provider call ledger. 0 = unlimited.</summary>
    public int DailyCallBudget { get; set; } = 500;

    /// <summary>Hard cap on MX HTTP calls per UTC month. 0 = unlimited.</summary>
    public int MonthlyCallBudget { get; set; } = 5000;

    /// <summary>Kill switch: when true NO call is sent to MX (config reload takes effect immediately).</summary>
    public bool CallsPaused { get; set; }

    /// <summary>
    /// Interval in milliseconds between polls of the member status endpoint when waiting
    /// for aggregation to complete after the Connect Widget flow. Set to 0 or a small value
    /// in tests to avoid artificial delays. Defaults to 3000ms (3 seconds).
    /// </summary>
    public int PollIntervalMs { get; set; } = 3_000;

    /// <summary>
    /// Maximum number of status-poll attempts when waiting for a member's aggregation to
    /// complete before giving up with a <see cref="NeedsUserActionProviderException"/>.
    /// Defaults to 10 (about 30 seconds at the default <see cref="PollIntervalMs"/>).
    /// </summary>
    public int MaxPollAttempts { get; set; } = 10;
}
