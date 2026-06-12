using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Dispatches domain events after aggregate persistence. Infrastructure provides
/// the concrete implementation; Application layer depends only on this abstraction.
/// Real handlers are wired per milestone; the no-op impl below is sufficient for M2.
/// </summary>
public interface IDomainEventDispatcher
{
    /// <summary>Dispatches all events in the list. Called by SaveChanges after persistence.</summary>
    Task DispatchAsync(IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken = default);
}
