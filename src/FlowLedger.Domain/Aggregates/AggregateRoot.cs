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
}
