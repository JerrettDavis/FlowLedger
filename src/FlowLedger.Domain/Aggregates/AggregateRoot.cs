using FlowLedger.SharedKernel;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// Base class for all aggregate roots. Provides strongly-typed domain event
/// collection management. Events are cleared after the application layer
/// dispatches them (typically post-persistence).
/// </summary>
public abstract class AggregateRoot : IAggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public abstract Guid Id { get; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void RaiseEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>Raises the event stamped with the caller-supplied time (from a <see cref="TimeProvider"/>), not the wall clock.</summary>
    protected void RaiseEvent(IDomainEvent domainEvent, DateTimeOffset occurredAt)
    {
        if (domainEvent is Events.DomainEventBase stamped)
        {
            stamped.StampOccurredAt(occurredAt);
        }

        _domainEvents.Add(domainEvent);
    }

    /// <summary>The instant to stamp on a change: from the supplied provider, or the system clock when the caller passes none.</summary>
    protected static DateTimeOffset Now(TimeProvider? time) => (time ?? TimeProvider.System).GetUtcNow();
}
