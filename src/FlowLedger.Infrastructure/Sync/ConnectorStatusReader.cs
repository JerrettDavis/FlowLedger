using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Sync;

internal sealed class ConnectorStatusReader(ProviderCallLedger ledger, FlowLedgerDbContext db, ITenantContext tenant) : IConnectorStatusReader
{
    public async Task<ConnectorUsage> GetUsageAsync(string providerName, CancellationToken ct = default)
    {
        var usage = await ledger.GetUsageAsync(providerName, DateTimeOffset.UtcNow, ct);
        return new ConnectorUsage(usage.CallsToday, usage.CallsThisMonth);
    }

    public Task<DateTimeOffset?> LastSyncedAtAsync(string providerName, CancellationToken ct = default)
        => SyncStatusReader.LastSyncedAtAsync(db, tenant, providerName, ct);
}
