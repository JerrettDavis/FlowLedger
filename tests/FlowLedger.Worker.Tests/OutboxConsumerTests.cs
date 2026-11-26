using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Events;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Worker.Tests;

/// <summary>
/// An event nobody consumes is dead weight in the outbox and hides a missing side effect (#92). Checked against the real
/// Worker composition, so a handler whose constructor dependencies the Worker cannot supply also fails here (it would make
/// the whole message undeliverable and dead-letter it).
/// </summary>
public sealed class OutboxConsumerTests
{
    [Fact]
    public async Task Every_domain_event_has_at_least_one_consumer_the_worker_can_construct()
    {
        using var host = WorkerHostBuilderFactory.BuildTestHost(b =>
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:flowledger"] = "Host=localhost;Database=unused;Username=u;Password=p",
            }));
        await using var scope = host.Services.CreateAsyncScope();

        var events = typeof(DomainEventBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToList();
        events.Should().NotBeEmpty();

        foreach (var type in events)
        {
            var handlers = scope.ServiceProvider.GetServices(typeof(IDomainEventHandler<>).MakeGenericType(type)).ToList();
            handlers.Should().NotBeEmpty($"{type.Name} is raised but has no consumer: add an IDomainEventHandler or remove the event");
        }
    }
}
