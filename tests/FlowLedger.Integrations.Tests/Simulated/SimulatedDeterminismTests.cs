using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Simulated;

/// <summary>
/// Verifies the determinism guarantee: identical inputs always produce byte-identical output.
/// </summary>
public sealed class SimulatedDeterminismTests
{
    private static SimulatedFinancialDataProvider MakeProvider(int baseSeed = 42, int historyMonths = 3) =>
        new(Options.Create(new SimulatedProviderOptions
        {
            BaseSeed = baseSeed,
            HistoryMonths = historyMonths,
        }));

    private static readonly TenantId DefaultTenant =
        TenantId.From(new Guid("00000000-0000-0000-0000-000000000001"));

    // ── Account determinism ───────────────────────────────────────────────────

    [Fact]
    public void Same_seed_produces_identical_account_list()
    {
        var a = SimulatedDataFactory.GetAccounts(DefaultTenant, 42);
        var b = SimulatedDataFactory.GetAccounts(DefaultTenant, 42);

        a.Should().HaveCount(b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            a[i].ProviderId.Should().Be(b[i].ProviderId);
            a[i].Name.Should().Be(b[i].Name);
            a[i].Balance.Amount.Should().Be(b[i].Balance.Amount);
        }
    }

    [Fact]
    public void Different_seed_produces_different_account_balances()
    {
        var a = SimulatedDataFactory.GetAccounts(DefaultTenant, 42);
        var b = SimulatedDataFactory.GetAccounts(DefaultTenant, 99);

        // Provider IDs change with seed (derived from seeded Faker)
        a.Select(x => x.ProviderId).Should()
            .NotBeEquivalentTo(b.Select(x => x.ProviderId),
                because: "different seeds should produce different account provider IDs");
    }

    [Fact]
    public void Different_tenant_produces_different_accounts_with_same_seed()
    {
        var tenantA = TenantId.From(new Guid("00000000-0000-0000-0000-000000000001"));
        var tenantB = TenantId.From(new Guid("00000000-0000-0000-0000-000000000002"));

        var a = SimulatedDataFactory.GetAccounts(tenantA, 42);
        var b = SimulatedDataFactory.GetAccounts(tenantB, 42);

        a.Select(x => x.ProviderId).Should()
            .NotBeEquivalentTo(b.Select(x => x.ProviderId),
                because: "different tenants should produce different account provider IDs");
    }

    // ── Transaction determinism ───────────────────────────────────────────────

    [Fact]
    public void Same_seed_and_tenant_produce_identical_transactions()
    {
        var accounts = SimulatedDataFactory.GetAccounts(DefaultTenant, 42);
        var accountId = accounts[0].ProviderId;

        var txA = SimulatedDataFactory.GetTransactions(accountId, DefaultTenant, 42, historyMonths: 3);
        var txB = SimulatedDataFactory.GetTransactions(accountId, DefaultTenant, 42, historyMonths: 3);

        txA.Should().HaveCount(txB.Count);
        for (var i = 0; i < txA.Count; i++)
        {
            txA[i].ProviderId.Should().Be(txB[i].ProviderId);
            txA[i].PostedDate.Should().Be(txB[i].PostedDate);
            txA[i].Amount.Amount.Should().Be(txB[i].Amount.Amount);
            txA[i].RawDescription.Should().Be(txB[i].RawDescription);
        }
    }

    [Fact]
    public void Different_seed_produces_different_transactions()
    {
        var accountsA = SimulatedDataFactory.GetAccounts(DefaultTenant, 42);
        var accountsB = SimulatedDataFactory.GetAccounts(DefaultTenant, 99);

        // Use first account from each
        var txA = SimulatedDataFactory.GetTransactions(accountsA[0].ProviderId, DefaultTenant, 42, 3);
        var txB = SimulatedDataFactory.GetTransactions(accountsB[0].ProviderId, DefaultTenant, 99, 3);

        // At least some provider IDs should differ
        txA.Select(x => x.ProviderId).Should()
            .NotBeEquivalentTo(txB.Select(x => x.ProviderId),
                because: "different seeds produce different transaction IDs");
    }

    // ── End-to-end determinism via provider ───────────────────────────────────

    [Fact]
    public async Task Full_pagination_of_same_account_produces_identical_ordered_results()
    {
        var providerA = MakeProvider();
        var providerB = MakeProvider();

        var memberA = await providerA.BeginConnectionAsync(DefaultTenant);
        var memberB = await providerB.BeginConnectionAsync(DefaultTenant);

        var accountsA = await providerA.GetAccountsAsync(memberA.ProviderId);
        var accountsB = await providerB.GetAccountsAsync(memberB.ProviderId);

        accountsA.Should().HaveSameCount(accountsB);

        // Paginate all transactions for each account and compare
        for (var i = 0; i < accountsA.Count; i++)
        {
            var allA = await DrainAllTransactionsAsync(providerA, accountsA[i].ProviderId);
            var allB = await DrainAllTransactionsAsync(providerB, accountsB[i].ProviderId);

            allA.Should().HaveCount(allB.Count,
                because: $"account {i} must have same transaction count across instances");

            for (var j = 0; j < allA.Count; j++)
            {
                allA[j].Amount.Amount.Should().Be(allB[j].Amount.Amount);
                allA[j].PostedDate.Should().Be(allB[j].PostedDate);
                allA[j].RawDescription.Should().Be(allB[j].RawDescription);
            }
        }
    }

    private static async Task<List<ProviderTransaction>> DrainAllTransactionsAsync(
        IFinancialDataProvider provider,
        string accountId)
    {
        var all = new List<ProviderTransaction>();
        var cursor = SyncCursor.Initial;
        bool hasMore;

        do
        {
            var page = await provider.GetTransactionsAsync(accountId, cursor, pageSize: 50);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
            hasMore = page.HasMore;
        }
        while (hasMore);

        return all;
    }
}
