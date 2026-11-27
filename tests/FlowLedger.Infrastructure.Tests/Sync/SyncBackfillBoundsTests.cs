using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// A long backfill must not keep the whole history in the change tracker (every SaveChanges rescans it, so growth is O(n^2)),
/// and the provider path obeys the per-tenant transaction ceiling like manual create and import (#163, #166).
/// </summary>
[Collection("Integration")]
public sealed class SyncBackfillBoundsTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public SyncBackfillBoundsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Backfill_keeps_the_change_tracker_bounded_and_persists_every_row()
    {
        const int pages = 30, pageSize = 100;
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var provider = new PagedProvider(db, pages, pageSize);
        var svc = new FinancialSyncService(provider, db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance);

        var result = await svc.SyncAsync();

        result.TransactionsAdded.Should().Be(pages * pageSize);
        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Transactions.CountAsync()).Should().Be(pages * pageSize);
        // Tracked rows at the start of any page: only earlier pages' still-pending rows (1 per page), never the whole history.
        provider.MaxTrackedTransactions.Should().BeLessThan(pages + pageSize,
            "committed rows are detached after each page; only pending rows may stay tracked");
    }

    [DockerFact]
    public async Task Sync_stops_with_a_classified_error_at_the_transaction_quota()
    {
        var tenant = TestTenantContext.New();
        await using var db = _fixture.CreateDbContext(tenant);
        var provider = new PagedProvider(db, pages: 5, pageSize: 100);
        var svc = new FinancialSyncService(provider, db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance)
        {
            MaxTransactionsPerTenant = 250,
        };

        var act = () => svc.SyncAsync();

        var ex = (await act.Should().ThrowAsync<NeedsUserActionProviderException>()).Which;
        ex.ProviderCode.Should().Be("transaction_quota_exceeded");
        ex.Message.Should().Contain("250");
        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Transactions.CountAsync()).Should().Be(200, "pages that fit were committed; the page that would pass the cap was not inserted");
    }

    private sealed class PagedProvider(FlowLedgerDbContext db, int pages, int pageSize) : IFinancialDataProvider
    {
        private int _page;
        public int MaxTrackedTransactions { get; private set; }
        public string ProviderName => "Simulated";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Accounts;
        public ConnectorTraits Traits => ConnectorTraits.PerTenantAccess | ConnectorTraits.ProvisionsLocally | ConnectorTraits.SeedsDemoData;
        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId t, CancellationToken ct = default)
            => Task.FromResult(new ProviderMemberRef("m1", "Bank", ConnectionStatus.Connected));
        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken ct = default)
            => Task.FromResult(ConnectionStatus.Connected);
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderAccount>>([
                new ProviderAccount("acct-1", "Checking", "CHECKING", new Money(100m, new Currency("USD")), null, "USD")]);

        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
        {
            MaxTrackedTransactions = Math.Max(MaxTrackedTransactions, db.ChangeTracker.Entries<Transaction>().Count());
            var start = _page * pageSize;
            var items = Enumerable.Range(start, pageSize)
                .Select(i => new ProviderTransaction(
                    $"t{i}", "acct-1", new DateOnly(2026, 1, 1).AddDays(i % 300), i % pageSize == 0,
                    new Money(-(1 + i), new Currency("USD")), $"MERCHANT {i}", $"Merchant {i}", null))
                .ToList();
            _page++;
            return Task.FromResult(new TransactionPage(items, new SyncCursor($"c{_page}"), _page < pages));
        }

        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
