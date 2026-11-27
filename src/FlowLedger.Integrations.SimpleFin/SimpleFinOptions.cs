namespace FlowLedger.Integrations.SimpleFin;

/// <summary>
/// SimpleFIN Bridge settings (config section <c>SimpleFin</c>).
/// The access URL embeds credentials: keep it in user-secrets / env vars, never in source, logs or the database.
/// </summary>
public sealed class SimpleFinOptions
{
    public const string SectionName = "SimpleFin";

    /// <summary>When true the SimpleFIN provider replaces Simulated/MX and the process is treated as PRODUCTION data.</summary>
    public bool Enabled { get; set; }

    /// <summary>Claimed access URL (https://user:pass@bridge.../simplefin). Produced by scripts/simplefin-claim.ps1.</summary>
    public string? AccessUrl { get; set; }

    /// <summary>History window requested per sync. SimpleFIN caps a request at 90 days.</summary>
    public int HistoryDays { get; set; } = 90;

    /// <summary>
    /// One-off deep backfill: when greater than <see cref="HistoryDays"/>, a sync pages backwards in &lt;=89-day windows
    /// (one request each) until this many days are covered or a window comes back empty, and ignores the cursor floor.
    /// Fingerprint dedupe makes re-importing safe. Leave 0 for normal operation.
    /// </summary>
    public int BackfillDays { get; set; }

    /// <summary>Days re-read before the cursor to catch late-posting items (fingerprint dedupe makes this safe).</summary>
    public int OverlapDays { get; set; } = 7;

    /// <summary>Hard cap on SimpleFIN requests per UTC day, counted from the provider call ledger (the Bridge itself allows about 24). 0 = unlimited.</summary>
    public int DailyCallBudget { get; set; } = 24;

    /// <summary>Hard cap on SimpleFIN requests per UTC month. 0 = unlimited.</summary>
    public int MonthlyCallBudget { get; set; }

    /// <summary>Kill switch: when true NO request is sent to SimpleFIN (config reload takes effect immediately).</summary>
    public bool CallsPaused { get; set; }
}
