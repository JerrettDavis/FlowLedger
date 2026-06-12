using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Events;
using FlowLedger.Infrastructure.Events;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Events;

/// <summary>
/// Unit tests for <see cref="DispatchingDomainEventDispatcher"/>.
/// All tests run in-process with no external dependencies.
/// </summary>
public sealed class DispatchingDomainEventDispatcherTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a dispatcher backed by the given service collection.
    /// </summary>
    private static DispatchingDomainEventDispatcher BuildDispatcher(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        return new DispatchingDomainEventDispatcher(
            provider,
            NullLogger<DispatchingDomainEventDispatcher>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatches_to_all_registered_handlers_for_event()
    {
        // Arrange — two handlers for the same event type.
        var callLog = new List<string>();

        var services = new ServiceCollection();
        services.AddScoped<IDomainEventHandler<AccountConnected>>(
            _ => new RecordingHandler<AccountConnected>(callLog, "handler-A"));
        services.AddScoped<IDomainEventHandler<AccountConnected>>(
            _ => new RecordingHandler<AccountConnected>(callLog, "handler-B"));

        var dispatcher = BuildDispatcher(services);

        var @event = new AccountConnected(
            new Domain.ValueObjects.AccountId(Guid.NewGuid()),
            new Domain.ValueObjects.TenantId(Guid.NewGuid()),
            Domain.Aggregates.AccountType.Checking);

        // Act
        await dispatcher.DispatchAsync([@event]);

        // Assert — both handlers were invoked.
        callLog.Should().BeEquivalentTo(["handler-A", "handler-B"]);
    }

    [Fact]
    public async Task Does_not_throw_when_no_handler_registered()
    {
        // Arrange — empty container; no handlers.
        var services = new ServiceCollection();
        var dispatcher = BuildDispatcher(services);

        var @event = new AccountConnected(
            new Domain.ValueObjects.AccountId(Guid.NewGuid()),
            new Domain.ValueObjects.TenantId(Guid.NewGuid()),
            Domain.Aggregates.AccountType.Checking);

        // Act & Assert — should complete without throwing.
        var act = () => dispatcher.DispatchAsync([@event]);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Handler_exception_is_logged_and_does_not_block_other_handlers()
    {
        // Arrange — first handler throws, second handler must still run.
        var callLog = new List<string>();

        var services = new ServiceCollection();
        services.AddScoped<IDomainEventHandler<AccountConnected>>(
            _ => new FaultingHandler<AccountConnected>("boom"));
        services.AddScoped<IDomainEventHandler<AccountConnected>>(
            _ => new RecordingHandler<AccountConnected>(callLog, "handler-after-fault"));

        var dispatcher = BuildDispatcher(services);

        var @event = new AccountConnected(
            new Domain.ValueObjects.AccountId(Guid.NewGuid()),
            new Domain.ValueObjects.TenantId(Guid.NewGuid()),
            Domain.Aggregates.AccountType.Savings);

        // Act — dispatcher must not throw even though one handler threw.
        var act = () => dispatcher.DispatchAsync([@event]);
        await act.Should().NotThrowAsync("a faulting handler must be caught and swallowed");

        // Assert — the handler registered after the faulting one was still invoked.
        callLog.Should().ContainSingle()
            .Which.Should().Be("handler-after-fault",
                "remaining handlers must execute after a preceding handler fault");
    }

    [Fact]
    public async Task Dispatches_multiple_events_in_order()
    {
        // Arrange
        var callLog = new List<string>();

        var services = new ServiceCollection();
        services.AddScoped<IDomainEventHandler<AccountConnected>>(
            _ => new RecordingHandler<AccountConnected>(callLog, "connected"));
        services.AddScoped<IDomainEventHandler<AccountBalanceUpdated>>(
            _ => new RecordingHandler<AccountBalanceUpdated>(callLog, "balance-updated"));

        var dispatcher = BuildDispatcher(services);

        var accountId = new Domain.ValueObjects.AccountId(Guid.NewGuid());
        var tenantId = new Domain.ValueObjects.TenantId(Guid.NewGuid());

        var events = new List<IDomainEvent>
        {
            new AccountConnected(accountId, tenantId, Domain.Aggregates.AccountType.Checking),
            new AccountBalanceUpdated(
                accountId, tenantId,
                new Domain.ValueObjects.Money(100m, new Domain.ValueObjects.Currency("USD")),
                new Domain.ValueObjects.Money(200m, new Domain.ValueObjects.Currency("USD"))),
        };

        // Act
        await dispatcher.DispatchAsync(events);

        // Assert — both events dispatched to their respective handlers.
        callLog.Should().BeEquivalentTo(["connected", "balance-updated"], o => o.WithStrictOrdering());
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class RecordingHandler<TEvent>(List<string> log, string name)
        : IDomainEventHandler<TEvent>
        where TEvent : IDomainEvent
    {
        public Task HandleAsync(TEvent domainEvent, CancellationToken ct = default)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingHandler<TEvent>(string message)
        : IDomainEventHandler<TEvent>
        where TEvent : IDomainEvent
    {
        public Task HandleAsync(TEvent domainEvent, CancellationToken ct = default)
            => throw new InvalidOperationException(message);
    }
}
