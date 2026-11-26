using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>Pending -> posted transitions must settle the existing row, never duplicate it.</summary>
[Collection("Integration")]
public sealed class PendingSettlementTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public PendingSettlementTests(IntegrationTestFixture fixture) => _fixture = fixture;
    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateOnly Today = new(2026, 11, 10);

    private static ProviderTransaction Tx(string id, bool pending, DateOnly date) =>
        new(id, "acct-1", date, pending, new Money(-42.50m, new Currency("USD")), "COFFEE SHOP", "Coffee", null);

    private async Task<(int Added, int Rows)> RunAsync(TestTenantContext tenant, params ProviderTransaction[] txs)
    {
        await using var db = _fixture.CreateDbContext(tenant);
        var svc = new FinancialSyncService(new ScriptedProvider(txs), db, tenant,
            new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance);
        var r = await svc.SyncAsync();
        await using var v = _fixture.CreateDbContext(tenant);
        return (r.TransactionsAdded, await v.Transactions.CountAsync());
    }

    [DockerFact]
    public async Task Same_provider_id_pending_then_posted_settles_in_place()
    {
        var tenant = TestTenantContext.New();
        (await RunAsync(tenant, Tx("t1", true, Today))).Should().Be((1, 1));
        var second = await RunAsync(tenant, Tx("t1", false, Today.AddDays(1)));
        second.Should().Be((0, 1));

        await using var v = _fixture.CreateDbContext(tenant);
        var row = await v.Transactions.SingleAsync();
        row.Status.Should().Be(TransactionStatus.Posted);
        row.PostedDate.Should().Be(Today.AddDays(1));
    }

    [DockerFact]
    public async Task Posted_record_with_new_id_absorbs_matching_pending_row()
    {
        var tenant = TestTenantContext.New();
        await RunAsync(tenant, Tx("pending-1", true, Today));
        var second = await RunAsync(tenant, Tx("posted-1", false, Today.AddDays(2)));
        second.Should().Be((0, 1));

        // A third sync with the posted id must dedupe (fingerprint was adopted).
        (await RunAsync(tenant, Tx("posted-1", false, Today.AddDays(2)))).Should().Be((0, 1));
    }

    private static ProviderTransaction TxAmount(string id, bool pending, DateOnly date, decimal amount, string desc = "RIVERSIDE GRILL")
        => new(id, "acct-1", date, pending, new Money(-amount, new Currency("USD")), desc, desc, null);

    [DockerFact]
    public async Task Posted_tip_total_absorbs_the_pending_authorization_instead_of_double_counting()
    {
        // #104: $20.00 authorization (P1) posts as $24.00 under a new id (P2) and P1 is never returned again.
        var tenant = TestTenantContext.New();
        await RunAsync(tenant, TxAmount("P1", true, Today, 20.00m));

        var second = await RunAsync(tenant, TxAmount("P2", false, Today.AddDays(1), 24.00m));

        second.Rows.Should().Be(1, "exactly one row must remain for the meal");
        await using var v = _fixture.CreateDbContext(tenant);
        var row = await v.Transactions.SingleAsync();
        row.Status.Should().Be(TransactionStatus.Posted);
        row.Amount.Amount.Should().Be(24.00m);
    }

    [DockerFact]
    public async Task Pending_row_the_provider_stopped_returning_is_excluded_after_the_ttl()
    {
        var tenant = TestTenantContext.New();
        var old = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-20);
        await RunAsync(tenant, TxAmount("HOLD", true, old, 100m, "FUEL PREAUTH"));

        // The hold vanished: later syncs return something unrelated.
        await RunAsync(tenant, TxAmount("OTHER", false, DateOnly.FromDateTime(DateTime.UtcNow), 5m, "UNRELATED"));

        await using var v = _fixture.CreateDbContext(tenant);
        var hold = await v.Transactions.SingleAsync(t => t.Description == "FUEL PREAUTH");
        hold.Status.Should().Be(TransactionStatus.Skipped, "stale pending holds must not count as spend");
    }

    [DockerFact]
    public async Task Recent_pending_row_is_left_alone()
    {
        var tenant = TestTenantContext.New();
        var recent = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        await RunAsync(tenant, TxAmount("HOLD2", true, recent, 100m, "HOTEL HOLD"));
        await RunAsync(tenant, TxAmount("OTHER2", false, DateOnly.FromDateTime(DateTime.UtcNow), 5m, "UNRELATED"));

        await using var v = _fixture.CreateDbContext(tenant);
        (await v.Transactions.SingleAsync(t => t.Description == "HOTEL HOLD")).Status.Should().Be(TransactionStatus.Pending);
    }

    private sealed class ScriptedProvider(ProviderTransaction[] txs) : IFinancialDataProvider
    {
        public string ProviderName => "Simulated";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Accounts;
        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId t, CancellationToken ct = default)
            => Task.FromResult(new ProviderMemberRef("m1", "Bank", ConnectionStatus.Connected));
        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken ct = default)
            => Task.FromResult(ConnectionStatus.Connected);
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderAccount>>([
                new ProviderAccount("acct-1", "Checking", "CHECKING",
                    new Money(100m, new Currency("USD")), null, "USD")]);
        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
            => Task.FromResult(new TransactionPage(txs, new SyncCursor("c"), false));
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
