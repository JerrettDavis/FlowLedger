using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// Proves a sync killed mid-way (provider throws on the Nth page) loses nothing and duplicates
/// nothing: after a restart the totals equal an uninterrupted run, and a further sync adds zero.
/// </summary>
[Collection("Integration")]
public sealed class SyncCrashResilienceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public SyncCrashResilienceTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<SyncResult> RunSync(TestTenantContext tenant, IFinancialDataProvider provider)
    {
        await using var db = _fixture.CreateDbContext(tenant);
        var svc = new FinancialSyncService(provider, db, tenant, new EfSyncCursorStore(db, tenant),
            NullLogger<FinancialSyncService>.Instance);
        return await svc.SyncAsync();
    }

    private static SimulatedFinancialDataProvider NewSim() =>
        new(Options.Create(new SimulatedProviderOptions()));

    [DockerFact]
    public async Task Crash_mid_sync_then_restart_matches_clean_run_with_no_loss_or_duplicates()
    {
        // Baseline: uninterrupted run on its own tenant.
        var clean = TestTenantContext.New();
        await RunSync(clean, NewSim());
        await using var cleanDb = _fixture.CreateDbContext(clean);
        var expected = await cleanDb.Transactions.CountAsync();
        expected.Should().BeGreaterThan(100, "need multiple pages for the crash to land mid-sync");

        // Crash run: provider dies on its 3rd transaction-page call.
        var crashed = TestTenantContext.New();
        var act = () => RunSync(crashed, new FaultingProvider(NewSim(), failOnTransactionPageCall: 3));
        await act.Should().ThrowAsync<IOException>();

        await using (var midDb = _fixture.CreateDbContext(crashed))
        {
            (await midDb.Transactions.CountAsync()).Should().BeLessThan(expected, "the crash left the sync partial");
        }

        // Restart with a healthy provider: must converge to exactly the clean-run totals.
        await RunSync(crashed, NewSim());
        await using (var doneDb = _fixture.CreateDbContext(crashed))
        {
            (await doneDb.Transactions.CountAsync()).Should().Be(expected);
            (await doneDb.Accounts.CountAsync()).Should().Be(await cleanDb.Accounts.CountAsync());
        }

        // And a further sync adds nothing.
        var again = await RunSync(crashed, NewSim());
        again.TransactionsAdded.Should().Be(0);
    }

    [DockerFact]
    public async Task Each_sync_appends_a_balance_snapshot_per_account()
    {
        var tenant = TestTenantContext.New();
        await RunSync(tenant, NewSim());
        await RunSync(tenant, NewSim());

        await using var db = _fixture.CreateDbContext(tenant);
        var accounts = await db.Accounts.CountAsync();
        (await db.BalanceSnapshots.CountAsync()).Should().Be(accounts * 2, "snapshots are append-only, one per account per sync");
    }

    [DockerFact]
    public async Task Throttle_clock_is_the_last_successful_sync_not_the_cursor_write_time()
    {
        var tenant = TestTenantContext.New();

        // A sync that dies after cursors were written must not stamp the success clock (#120).
        var act = () => RunSync(tenant, new FaultingProvider(NewSim(), failOnTransactionPageCall: 3));
        await act.Should().ThrowAsync<IOException>();
        await using (var failedDb = _fixture.CreateDbContext(tenant))
        {
            (await failedDb.SyncCursors.CountAsync()).Should().BeGreaterThan(0, "cursors advanced before the failure");
            (await SyncStatusReader.LastSyncedAtAsync(failedDb, tenant, "Simulated", default))
                .Should().BeNull("a failed sync is not a successful sync, so it does not start a throttle window");
        }

        await RunSync(tenant, NewSim());
        await using var doneDb = _fixture.CreateDbContext(tenant);
        var last = await SyncStatusReader.LastSyncedAtAsync(doneDb, tenant, "Simulated", default);
        last.Should().NotBeNull();
    }

    private sealed class FaultingProvider(IFinancialDataProvider inner, int failOnTransactionPageCall) : IFinancialDataProvider
    {
        private int _calls;
        public string ProviderName => inner.ProviderName;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId t, CancellationToken ct = default) => inner.BeginConnectionAsync(t, ct);
        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken ct = default) => inner.GetConnectionStatusAsync(m, ct);
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken ct = default) => inner.GetAccountsAsync(m, ct);

        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
        {
            if (++_calls == failOnTransactionPageCall)
            {
                throw new IOException("simulated crash");
            }

            return inner.GetTransactionsAsync(a, c, p, ct);
        }

        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => inner.VerifyWebhookAsync(b, s, ct);
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => inner.ParseWebhookAsync(b, ct);
    }
}
