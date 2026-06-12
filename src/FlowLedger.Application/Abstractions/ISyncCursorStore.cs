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
        string providerName,
        string providerAccountId,
        CancellationToken ct = default);

    /// <summary>
    /// Persists (upserts) the cursor value for the given provider and account.
    /// Idempotent: calling with the same key multiple times always stores the latest value.
    /// </summary>
    Task SetAsync(
        string providerName,
        string providerAccountId,
        string cursorValue,
        CancellationToken ct = default);
}
