using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>
/// Stateful model test for INV-02 (#178): whatever sequence of pages the provider replays (any subset of its transactions, in any order,
/// with repeats inside a page and across syncs), the stored set equals exactly the set of distinct provider transactions delivered so far,
/// and a sync reports as added only the ones it had not seen. The model is a plain set; the system under test is the real
/// <see cref="FinancialSyncService"/> against Postgres. Deterministic: a fixed list of seeds drives <see cref="Random"/>.
/// </summary>
[Collection("Integration")]
[Trait("Invariant", "INV-02")]
public sealed class SyncIdempotencyModelTests : IAsyncLifetime
{
    private const int Universe = 24;
    private const int StepsPerRun = 8;

    private readonly IntegrationTestFixture _fixture;
    public SyncIdempotencyModelTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 12).Select(s => new object[] { s });

    /// <summary>The provider's full history: every transaction has its own id, amount and description, so none is a duplicate of another.</summary>
    private static ProviderTransaction[] History() => Enumerable.Range(0, Universe)
        .Select(i => new ProviderTransaction(
            $"tx-{i}", "acct-1", TestClock.Today.AddDays(-(i % 17)), IsPending: false,
            new Money(-(10m + i * 1.37m), new Currency("USD")), $"MERCHANT {i:D2} PURCHASE", $"Merchant {i:D2}", null))
        .ToArray();

    [DockerTheory]
    [MemberData(nameof(Seeds))]
    public async Task Replaying_any_page_sequence_stores_exactly_the_distinct_transactions_delivered(int seed)
    {
        var history = History();
        var rng = new Random(seed);
        var tenant = TestTenantContext.New();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var step = 0; step < StepsPerRun; step++)
        {
            // A page: a random-size random sample of the history, shuffled, with some members repeated.
            var page = history.OrderBy(_ => rng.Next()).Take(rng.Next(0, Universe + 1)).ToList();
            page.AddRange(page.Where(_ => rng.Next(4) == 0).ToList());
            page = page.OrderBy(_ => rng.Next()).ToList();

            var expectedNew = page.Select(t => t.ProviderId!).Distinct().Count(id => !seen.Contains(id));
            var result = await SyncAsync(tenant, page);
            foreach (var t in page)
            {
                seen.Add(t.ProviderId!);
            }

            result.TransactionsAdded.Should().Be(expectedNew, "step {0} of seed {1}: only transactions not seen before are added", step, seed);

            await using var v = _fixture.CreateDbContext(tenant);
            var stored = await v.Transactions.Select(t => t.Description).ToListAsync();
            stored.Should().OnlyHaveUniqueItems("a replay never stores a transaction twice (seed {0}, step {1})", seed, step);
            stored.Should().BeEquivalentTo(
                history.Where(t => seen.Contains(t.ProviderId!)).Select(t => t.MerchantName!),
                "the stored set is the set of distinct provider transactions delivered so far (seed {0}, step {1})", seed, step);
        }

        // Replaying the whole history, in reverse, adds only what was never delivered; replaying it again adds nothing.
        var never = history.Count(t => !seen.Contains(t.ProviderId!));
        (await SyncAsync(tenant, history.Reverse().ToList())).TransactionsAdded.Should().Be(never);
        (await SyncAsync(tenant, history.ToList())).TransactionsAdded.Should().Be(0);

        await using var final = _fixture.CreateDbContext(tenant);
        (await final.Transactions.CountAsync()).Should().Be(Universe);
    }

    private async Task<SyncResult> SyncAsync(TestTenantContext tenant, IReadOnlyList<ProviderTransaction> page)
    {
        await using var db = _fixture.CreateDbContext(tenant);
        var svc = new FinancialSyncService(
            new PagedProvider(page), db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance, clock: TestClock.Provider);
        return await svc.SyncAsync();
    }

    private sealed class PagedProvider(IReadOnlyList<ProviderTransaction> page) : IFinancialDataProvider
    {
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
            => Task.FromResult(new TransactionPage(page, new SyncCursor("c"), false));
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
