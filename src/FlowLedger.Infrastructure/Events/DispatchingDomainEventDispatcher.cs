using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Events;

/// <summary>
/// Real domain-event dispatcher. For each raised <see cref="IDomainEvent"/> it resolves all
/// <see cref="IDomainEventHandler{TEvent}"/> registrations that match the event's runtime type
/// (closed-generic resolution via reflection) and invokes them sequentially.
///
/// Error behaviour (explicit, covered by tests):
///   A handler that throws is logged at Error level and the exception is swallowed so that
///   remaining handlers still execute.  The outer <see cref="SaveChangesAsync"/> call does NOT
///   see the handler exception — domain-event side effects are treated as non-critical,
///   best-effort operations.  If strong delivery guarantees are needed in a later milestone,
///   replace this dispatcher with an outbox-pattern variant.
/// </summary>
internal sealed class DispatchingDomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DispatchingDomainEventDispatcher> _logger;

    public DispatchingDomainEventDispatcher(
        IServiceProvider services,
        ILogger<DispatchingDomainEventDispatcher> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task DispatchAsync(
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        foreach (var domainEvent in events)
        {
            var eventType = domainEvent.GetType();
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);

            IEnumerable<object?> handlers;
            try
            {
                handlers = _services.GetServices(handlerType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to resolve handlers for domain event {EventType} ({EventId})",
                    eventType.Name, domainEvent.EventId);
                continue;
            }

            foreach (var handler in handlers)
            {
                if (handler is null)
                {
                    continue;
                }

                try
                {
                    // Invoke HandleAsync via the closed-generic interface.
                    var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;
                    var task = (Task)handleMethod.Invoke(handler, [domainEvent, cancellationToken])!;
                    await task;
                }
                catch (Exception ex)
                {
                    // Log and continue — one faulty handler must not block the others.
                    _logger.LogError(ex,
                        "Handler {HandlerType} faulted processing domain event {EventType} ({EventId}). " +
                        "The exception is suppressed; remaining handlers will still execute.",
                        handler.GetType().Name, eventType.Name, domainEvent.EventId);
                }
            }
        }
    }
}
