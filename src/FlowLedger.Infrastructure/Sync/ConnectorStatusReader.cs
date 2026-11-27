using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.HostStores;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Sync;

internal sealed class ConnectorStatusReader(ProviderCallLedger ledger, FlowLedgerDbContext db, ITenantContext tenant) : IConnectorStatusReader
{
    public async Task<ConnectorUsage> GetUsageAsync(ConnectorId connector, CancellationToken ct = default)
    {
        var usage = await ledger.GetUsageAsync(connector.Value, DateTimeOffset.UtcNow, ct);
        return new ConnectorUsage(usage.CallsToday, usage.CallsThisMonth);
    }

    public Task<DateTimeOffset?> LastSyncedAtAsync(ConnectorId connector, CancellationToken ct = default)
        => SyncStatusReader.LastSyncedAtAsync(db, tenant, connector.Value, ct);
}
