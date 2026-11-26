using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Stages a domain event in the transactional outbox (#92) for events that are not raised by an aggregate (an import or a sync
/// finishing). Call <see cref="Enqueue"/> before the save that should carry the row so it commits atomically with the data,
/// or <see cref="FlushAsync"/> after the unit of work to commit it on its own.
/// Delivery is at-least-once through <see cref="IDomainEventHandler{TEvent}"/> consumers.
/// </summary>
public interface IOutbox
{
    /// <summary>Stages the event; it commits with the next save of the current unit of work.</summary>
    void Enqueue(IDomainEvent domainEvent);

    /// <summary>Commits the staged events on their own, once the unit of work that produced them is finished (it drops the context's change tracking).</summary>
    Task FlushAsync(CancellationToken ct = default);
}
