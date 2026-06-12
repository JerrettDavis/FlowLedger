using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Handles a specific domain event type. Infrastructure discovers and invokes all
/// registered handlers for each event via <see cref="IDomainEventDispatcher"/>.
/// </summary>
public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct = default);
}
