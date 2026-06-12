namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// Lifecycle states for a provider member/connection.
/// Mirrors the sync lifecycle defined in PLAN §7.3.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>No institution has been linked yet.</summary>
    NotConnected,

    /// <summary>OAuth / Connect flow has been initiated but not completed.</summary>
    ConnectionPending,

    /// <summary>Connected and credentials are valid.</summary>
    Connected,

    /// <summary>A background sync job is actively running.</summary>
    Syncing,

    /// <summary>Last sync completed without errors.</summary>
    Healthy,

    /// <summary>Sync completed but with recoverable warnings (e.g. partial data).</summary>
    Degraded,

    /// <summary>The institution requires the user to re-authenticate or take action.</summary>
    NeedsUserAction,

    /// <summary>The connection has been explicitly disabled by the user or admin.</summary>
    Disabled,

    /// <summary>An unrecoverable error occurred; manual intervention required.</summary>
    Error,
}
