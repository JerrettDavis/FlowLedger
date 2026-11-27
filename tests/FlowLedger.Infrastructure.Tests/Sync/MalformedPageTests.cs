using System.Globalization;
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

/// <summary>One malformed provider page must not wedge a connection: repeats are skipped, unexpected failures leave an issue behind.</summary>
[Collection("Integration")]
public sealed class MalformedPageTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public MalformedPageTests(IntegrationTestFixture fixture) => _fixture = fixture;
    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateOnly Day = new(2026, 11, 10);

    private static ProviderTransaction Tx(string id, bool pending = false) =>
        new(id, "acct-1", Day, pending, new Money(-42.50m, new Currency("USD")), "COFFEE SHOP", "Coffee", null);

    private async Task<SyncResult> RunAsync(TestTenantContext tenant, ScriptedProvider provider)
    {
        await using var db = _fixture.CreateDbContext(tenant);
        var store = new EfProviderConnectionStore(db, tenant);
        await store.SaveAsync(provider.ProviderName, "m1", "Bank", "Active");
        var svc = new FinancialSyncService(provider, db, tenant, new EfSyncCursorStore(db, tenant),
            NullLogger<FinancialSyncService>.Instance, health: store);
        return await svc.SyncAsync();
    }

    [DockerFact]
    public async Task Same_id_twice_in_one_page_inserts_one_row_and_skips_the_repeat()
    {
        var tenant = TestTenantContext.New();
        var result = await RunAsync(tenant, new ScriptedProvider([[Tx("t1"), Tx("t1")]]));

        result.TransactionsAdded.Should().Be(1);
        result.TransactionsSkipped.Should().Be(1);
        await using var v = _fixture.CreateDbContext(tenant);
        (await v.Transactions.CountAsync()).Should().Be(1);
    }

    [DockerFact]
    public async Task Same_id_on_two_pages_of_one_sync_is_not_inserted_twice_and_the_posted_copy_settles_it()
    {
        var tenant = TestTenantContext.New();
        var result = await RunAsync(tenant, new ScriptedProvider([[Tx("t1", pending: true)], [Tx("t1")]]));

        result.TransactionsAdded.Should().Be(1);
        await using var v = _fixture.CreateDbContext(tenant);
        var row = await v.Transactions.SingleAsync();
        row.Status.Should().Be(TransactionStatus.Posted);
    }

    [DockerFact]
    public async Task An_unexpected_exception_is_recorded_as_an_internal_issue_and_counted()
    {
        var tenant = TestTenantContext.New();
        var provider = new ScriptedProvider([]) { Throw = new InvalidOperationException("boom") };

        for (var i = 1; i <= 2; i++)
        {
            var act = () => RunAsync(tenant, provider);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var v = _fixture.CreateDbContext(tenant);
        var row = await v.ProviderConnections.AsNoTracking().SingleAsync();
        row.IssueKind.Should().Be(nameof(ProviderIssueKind.Internal));
        row.ConsecutiveFailures.Should().Be(2);
    }

    private sealed class ScriptedProvider(ProviderTransaction[][] pages) : IFinancialDataProvider
    {
        public Exception? Throw { get; init; }
        public string ProviderName => "Simulated";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Accounts;
        public ConnectorTraits Traits => ConnectorTraits.PerTenantAccess | ConnectorTraits.ProvisionsLocally | ConnectorTraits.SeedsDemoData;
        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId t, CancellationToken ct = default)
            => Task.FromResult(new ProviderMemberRef("m1", "Bank", ConnectionStatus.Connected));
        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken ct = default)
            => Task.FromResult(ConnectionStatus.Connected);
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken ct = default)
            => Throw is not null
                ? Task.FromException<IReadOnlyList<ProviderAccount>>(Throw)
                : Task.FromResult<IReadOnlyList<ProviderAccount>>([
                    new ProviderAccount("acct-1", "Checking", "CHECKING", new Money(100m, new Currency("USD")), null, "USD")]);
        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
        {
            var index = c == SyncCursor.Initial ? 0 : int.Parse(c.Value);
            var items = index < pages.Length ? pages[index] : [];
            return Task.FromResult(new TransactionPage(items, new SyncCursor((index + 1).ToString(CultureInfo.InvariantCulture)), index + 1 < pages.Length));
        }
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
