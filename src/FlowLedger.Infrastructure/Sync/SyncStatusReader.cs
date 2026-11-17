using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>Read-only "when did we last sync" lookup (no provider call), shared by the API status endpoint.</summary>
public static class SyncStatusReader
{
    public static async Task<DateTimeOffset?> LastSyncedAtAsync(
        FlowLedgerDbContext db, FlowLedger.SharedKernel.ITenantContext tenantContext, string providerName, CancellationToken ct)
    {
        var tenant = tenantContext.TenantId;
        return await db.SyncCursors
            .Where(c => c.TenantId == tenant && c.ProviderName == providerName)
            .MaxAsync(c => (DateTimeOffset?)c.LastSyncedAt, ct);
    }
}
