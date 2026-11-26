using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Events;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Events;

/// <summary>Transactional outbox (#92): events are committed with the aggregate and delivered at-least-once with retry, per-handler idempotence and dead-lettering.</summary>
[Collection("Integration")]
public sealed class OutboxTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ManualTime _time = new();

    public OutboxTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow.AddMinutes(1); // ahead of event times so freshly raised events are already due
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Handler(string name, List<string> log, Func<int, bool> fails) : IDomainEventHandler<AccountConnected>
    {
        public int Calls;
        public Task HandleAsync(AccountConnected domainEvent, CancellationToken ct = default)
        {
            Calls++;
            if (fails(Calls))
            {
                throw new InvalidOperationException($"{name} boom");
            }

            lock (log)
            {
                log.Add(name);
            }

            return Task.CompletedTask;
        }
    }

    // Distinct types so each has its own completion key.
    private sealed class HandlerA(List<string> log, Func<int, bool> fails) : IDomainEventHandler<AccountConnected>
    {
        private readonly Handler _inner = new("A", log, fails);
        public Task HandleAsync(AccountConnected e, CancellationToken ct = default) => _inner.HandleAsync(e, ct);
    }

    private sealed class HandlerB(List<string> log, Func<int, bool> fails) : IDomainEventHandler<AccountConnected>
    {
        private readonly Handler _inner = new("B", log, fails);
        public Task HandleAsync(AccountConnected e, CancellationToken ct = default) => _inner.HandleAsync(e, ct);
    }

    private OutboxProcessor Processor(TestTenantContext tenant, List<string> log, Func<int, bool> aFails, Func<int, bool> bFails, OutboxOptions? options = null)
    {
        var services = new ServiceCollection();
        // Singletons so failure counters persist across polls (the real handlers are scoped but stateless).
        var a = new HandlerA(log, aFails);
        var b = new HandlerB(log, bFails);
        services.AddSingleton<IDomainEventHandler<AccountConnected>>(a);
        services.AddSingleton<IDomainEventHandler<AccountConnected>>(b);
        var provider = services.BuildServiceProvider();
        return new OutboxProcessor(_fixture.CreateProductionStyleContext(tenant), provider,
            NullLogger<OutboxProcessor>.Instance, options ?? new OutboxOptions(), _time);
    }

    private async Task RaiseAsync(TestTenantContext tenant)
    {
        await using var ctx = _fixture.CreateProductionStyleContext(tenant);
        var account = Account.Create(TenantId.From(tenant.TenantId), "Checking", AccountType.Checking, new Money(0m, Currency.Usd));
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
    }

    private async Task<(int Attempts, bool Processed, bool Dead, string? Error)[]> RowsAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT attempts, processed_utc IS NOT NULL, dead_lettered_utc IS NOT NULL, last_error FROM outbox_messages ORDER BY occurred_utc", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(int, bool, bool, string?)>();
        while (await r.ReadAsync())
        {
            rows.Add((r.GetInt32(0), r.GetBoolean(1), r.GetBoolean(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }

        return [.. rows];
    }

    [DockerFact]
    public async Task Saving_an_aggregate_writes_the_event_to_the_outbox_in_the_same_transaction()
    {
        var tenant = TestTenantContext.New();
        await RaiseAsync(tenant);

        var rows = await RowsAsync();
        rows.Should().ContainSingle().Which.Should().Be((0, false, false, (string?)null));
    }

    [DockerFact]
    public async Task A_failed_save_leaves_no_outbox_row()
    {
        var tenant = TestTenantContext.New();
        await using var ctx = _fixture.CreateProductionStyleContext(tenant);
        var account = Account.Create(TenantId.From(tenant.TenantId), "Dup", AccountType.Checking, new Money(0m, Currency.Usd));
        ctx.Accounts.Add(account);
        // Cross-tenant mutation is rejected before commit: nothing may be recorded.
        ctx.Accounts.Add(Account.Create(TenantId.From(Guid.NewGuid()), "Other", AccountType.Checking, new Money(0m, Currency.Usd)));
        var act = () => ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await RowsAsync()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Crash_after_commit_still_delivers_from_a_fresh_dispatcher()
    {
        var tenant = TestTenantContext.New();
        await RaiseAsync(tenant); // the "crash": nothing was dispatched inline

        var log = new List<string>();
        var processor = Processor(tenant, log, _ => false, _ => false);
        (await processor.ProcessBatchAsync()).Should().Be(1);

        log.Should().BeEquivalentTo(["A", "B"]);
        (await RowsAsync()).Single().Processed.Should().BeTrue();
        (await processor.ProcessBatchAsync()).Should().Be(0, "a processed message is never delivered again");
    }

    [DockerFact]
    public async Task A_failing_handler_is_retried_not_swallowed_and_successful_handlers_do_not_repeat()
    {
        var tenant = TestTenantContext.New();
        await RaiseAsync(tenant);
        var log = new List<string>();
        var processor = Processor(tenant, log, _ => false, call => call == 1); // B fails its first call only

        await processor.ProcessBatchAsync();
        var afterFirst = (await RowsAsync()).Single();
        afterFirst.Processed.Should().BeFalse("a handler failure must leave the message pending");
        afterFirst.Attempts.Should().Be(1);
        afterFirst.Error.Should().Contain("B boom");
        log.Should().Equal("A");

        (await processor.ProcessBatchAsync()).Should().Be(0, "backoff holds the retry back");

        _time.Advance(TimeSpan.FromMinutes(1));
        (await processor.ProcessBatchAsync()).Should().Be(1);

        log.Should().Equal("A", "B");
        (await RowsAsync()).Single().Processed.Should().BeTrue();
    }

    [DockerFact]
    public async Task A_message_is_dead_lettered_after_the_attempt_cap()
    {
        var tenant = TestTenantContext.New();
        await RaiseAsync(tenant);
        var processor = Processor(tenant, [], _ => true, _ => false, new OutboxOptions { MaxAttempts = 3 });

        for (var i = 0; i < 5; i++)
        {
            await processor.ProcessBatchAsync();
            _time.Advance(TimeSpan.FromHours(1));
        }

        var row = (await RowsAsync()).Single();
        row.Dead.Should().BeTrue();
        row.Processed.Should().BeFalse();
        row.Attempts.Should().Be(3);
        row.Error.Should().Contain("A boom");
    }

    [DockerFact]
    public async Task Dispatch_is_tenant_scoped()
    {
        var one = TestTenantContext.New();
        var two = TestTenantContext.New();
        await RaiseAsync(one);
        await RaiseAsync(two);

        var log = new List<string>();
        (await Processor(one, log, _ => false, _ => false).ProcessBatchAsync()).Should().Be(1);

        (await RowsAsync()).Count(r => r.Processed).Should().Be(1, "tenant two's message is untouched");
        (await Processor(two, log, _ => false, _ => false).ProcessBatchAsync()).Should().Be(1);
        (await RowsAsync()).Should().OnlyContain(r => r.Processed);
    }

    [DockerFact]
    public async Task Concurrent_dispatchers_never_deliver_the_same_message_twice()
    {
        var tenant = TestTenantContext.New();
        for (var i = 0; i < 8; i++)
        {
            await RaiseAsync(tenant);
        }

        var log = new List<string>();
        var processors = Enumerable.Range(0, 4).Select(_ => Processor(tenant, log, _ => false, _ => false)).ToList();
        await Task.WhenAll(processors.Select(async p =>
        {
            while (await p.ProcessBatchAsync() > 0)
            {
            }
        }));

        (await RowsAsync()).Should().OnlyContain(r => r.Processed);
        lock (log)
        {
            log.Count(x => x == "A").Should().Be(8);
            log.Count(x => x == "B").Should().Be(8);
        }
    }

    [DockerFact]
    public async Task An_expired_lease_from_a_crashed_dispatcher_is_reclaimed()
    {
        var tenant = TestTenantContext.New();
        await RaiseAsync(tenant);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE outbox_messages SET lock_token = gen_random_uuid(), locked_until_utc = now() + interval '1 minute'", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var log = new List<string>();
        var processor = Processor(tenant, log, _ => false, _ => false, new OutboxOptions { Lease = TimeSpan.FromMinutes(2) });
        (await processor.ProcessBatchAsync()).Should().Be(0, "a live lease is respected");

        _time.Advance(TimeSpan.FromMinutes(5));
        // The fake clock only moves forward from wall time, so the lease written with now() is already behind it.
        (await processor.ProcessBatchAsync()).Should().Be(1);
        log.Should().BeEquivalentTo(["A", "B"]);
    }

    [Fact]
    public void Every_domain_event_round_trips_through_the_outbox_serializer_with_its_event_id()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var usd = Currency.Usd;
        var events = new FlowLedger.SharedKernel.IDomainEvent[]
        {
            new AccountConnected(AccountId.From(Guid.NewGuid()), tenant, AccountType.Checking),
            new AccountBalanceUpdated(AccountId.From(Guid.NewGuid()), tenant, new Money(1m, usd), new Money(2.5m, usd)),
            new TransactionImported(TransactionId.From(Guid.NewGuid()), tenant, AccountId.From(Guid.NewGuid()), new Money(9.99m, usd),
                TransactionDirection.Debit, TransactionSource.Manual),
        };

        foreach (var e in events)
        {
            var back = OutboxEventSerializer.Deserialize(OutboxEventSerializer.TypeName(e), OutboxEventSerializer.Serialize(e));
            back.Should().Be(e, "records compare by value, so every field and the original EventId and OccurredAt survive");
            back.EventId.Should().Be(e.EventId);
        }
    }
}
