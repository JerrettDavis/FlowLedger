using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// No-op implementation of <see cref="IDomainEventDispatcher"/>.
/// Domain events are collected and cleared but not yet routed to handlers.
/// Real handler wiring will be added in later milestones when application feature
/// handlers are implemented.
/// </summary>
public sealed class NoOpDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
