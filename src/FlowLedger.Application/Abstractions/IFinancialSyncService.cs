namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Orchestrates a full sync cycle against the configured financial data provider:
/// fetches provider accounts, maps and upserts domain Accounts, then fetches paginated
/// transactions with cursor-based incremental support, deduplicates via fingerprint,
/// and persists new records.
/// </summary>
public interface IFinancialSyncService
{
    /// <summary>
    /// Connects a new provider member for the current tenant and syncs initial data.
    /// Returns the provider member id issued by the provider.
    /// </summary>
    Task<string> ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs an incremental sync for all connected accounts under the current tenant.
    /// Idempotent: re-running never creates duplicates.
    /// </summary>
    /// <returns>Summary of how many accounts + transactions were upserted.</returns>
    Task<SyncResult> SyncAsync(CancellationToken ct = default);
}

/// <summary>Summary returned after a sync run.</summary>
public sealed record SyncResult(
    int AccountsUpserted,
    int TransactionsAdded,
    int TransactionsSkipped,
    int RecurringFlowsAdded);
