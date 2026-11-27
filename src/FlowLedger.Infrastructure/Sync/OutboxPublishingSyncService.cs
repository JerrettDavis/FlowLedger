using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Outermost sync decorator (#92, #175, #180): after a sync that succeeded it records <see cref="SyncCompleted"/> in the
/// transactional outbox, whoever started the sync (API, on-demand job, scheduled Worker). Everything that must follow a sync
/// (webhooks, duplicate merge, bill-sheet refresh, Home Assistant alerts) is a retried, idempotent outbox consumer of that event
/// rather than an inline step that is lost when it fails. The event is committed right after the sync's own commits, so it is
/// durable but not in the same transaction as the sync's data; a crash in that gap loses the follow-ups until the next sync.
/// </summary>
public sealed class OutboxPublishingSyncService(IFinancialSyncService inner, IOutbox outbox, ITenantContext tenant, TimeProvider clock) : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = await inner.SyncAsync(ct);
        outbox.Enqueue(new SyncCompleted(TenantId.From(tenant.TenantId), result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped) { OccurredAt = clock.GetUtcNow() });
        await outbox.FlushAsync(ct);
        return result;
    }
}
