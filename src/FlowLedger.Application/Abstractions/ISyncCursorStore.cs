using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Durable store for sync cursor bookmarks.
/// Persists the high-water mark per provider and account so incremental syncs can resume
/// after a process restart without re-importing already-seen transactions.
///
/// Cursor values are treated as opaque strings at this boundary; the Infrastructure
/// implementation converts to/from provider-specific cursor types (e.g. SyncCursor).
/// </summary>
public interface ISyncCursorStore
{
    /// <summary>
    /// Returns the stored cursor value for the given provider and account,
    /// or an empty string when no cursor has been persisted yet (initial load).
    /// </summary>
    Task<string> GetAsync(
        ConnectorId connector,
        string providerAccountId,
        CancellationToken ct = default);

    /// <summary>
    /// Persists (upserts) the cursor value for the given provider and account.
    /// Idempotent: calling with the same key multiple times always stores the latest value.
    /// </summary>
    Task SetAsync(
        ConnectorId connector,
        string providerAccountId,
        string cursorValue,
        CancellationToken ct = default);

    /// <summary>
    /// Stamps the accounts' cursors as successfully synced now (#120). Called once, after a whole sync completed; the throttle
    /// window and the "last synced" display read this, never the cursor-write time, which also advances on a failed sync.
    /// </summary>
    Task MarkSuccessfulAsync(
        ConnectorId connector,
        IReadOnlyCollection<string> providerAccountIds,
        CancellationToken ct = default);
}
