using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Events;

/// <summary>Stages outbox rows on the request's <see cref="FlowLedgerDbContext"/>, so they commit atomically with whatever else the unit of work saves.</summary>
internal sealed class EfOutbox(FlowLedgerDbContext db, ITenantContext tenant) : IOutbox
{
    private readonly List<OutboxMessageRecord> _staged = [];

    public void Enqueue(IDomainEvent domainEvent)
    {
        var row = OutboxMessageRecord.Create(
            domainEvent.EventId, tenant.TenantId, OutboxEventSerializer.TypeName(domainEvent),
            OutboxEventSerializer.Serialize(domainEvent), domainEvent.OccurredAt);
        _staged.Add(row);
        db.OutboxMessages.Add(row);
    }

    /// <summary>
    /// Commits the staged events on their own. The unit of work is finished by now (a sync leaves detached and half-detached rows
    /// in the tracker, which a broad save would trip over), so tracking is dropped and only the outbox rows are written.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var pending = _staged.Where(r => db.Entry(r).State == EntityState.Added).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        db.ChangeTracker.Clear();
        db.OutboxMessages.AddRange(pending);
        await db.SaveChangesAsync(ct);
        _staged.RemoveAll(pending.Contains);
    }
}
