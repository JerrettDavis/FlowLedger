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

    /// <summary>Days re-read before the cursor to catch late-posting items (fingerprint dedupe makes this safe).</summary>
    public int OverlapDays { get; set; } = 7;
}
