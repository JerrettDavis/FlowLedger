using FlowLedger.Application.Abstractions;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Sync for a tenant that has no provider credentials of its own. The configured provider credentials (e.g. the
/// SimpleFIN access URL) belong to the household/legacy tenant only; syncing them into another user's tenant would
/// leak one user's bank data into another's. Such tenants sync nothing until they link their own institution.
/// </summary>
internal sealed class UnlinkedTenantSyncService : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "This account has no linked financial institution yet. Per-user provider linking is not available; the configured provider belongs to the household owner.");

    public Task<SyncResult> SyncAsync(CancellationToken ct = default) => Task.FromResult(new SyncResult(0, 0, 0, 0));
}
