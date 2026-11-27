using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>#65: a second sync over unchanged data makes no more provider calls than the "would make N calls" estimate.</summary>
[Collection("Integration")]
public sealed class SyncCallCountTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class CountingProvider(IFinancialDataProvider inner) : IFinancialDataProvider
    {
        public int Begin;
        public int Status;
        public int Accounts;
        public int Transactions;

        public int Total => Begin + Status + Accounts + Transactions;

        public string ProviderName => inner.ProviderName;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ConnectorTraits Traits => inner.Traits;

        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId tenantId, CancellationToken cancellationToken = default)
        {
            Begin++;
            return inner.BeginConnectionAsync(tenantId, cancellationToken);
        }

        public Task<ConnectionStatus> GetConnectionStatusAsync(string memberProviderId, CancellationToken cancellationToken = default)
        {
            Status++;
            return inner.GetConnectionStatusAsync(memberProviderId, cancellationToken);
        }

        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string memberProviderId, CancellationToken cancellationToken = default)
        {
            Accounts++;
            return inner.GetAccountsAsync(memberProviderId, cancellationToken);
        }

        public Task<TransactionPage> GetTransactionsAsync(string providerAccountId, SyncCursor cursor, int pageSize = 100, CancellationToken cancellationToken = default)
        {
            Transactions++;
            return inner.GetTransactionsAsync(providerAccountId, cursor, pageSize, cancellationToken);
        }

        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> rawBody, string signatureHeader, CancellationToken cancellationToken = default)
            => inner.VerifyWebhookAsync(rawBody, signatureHeader, cancellationToken);

        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
            => inner.ParseWebhookAsync(rawBody, cancellationToken);
    }

    private static FinancialSyncService Service(FlowLedgerDbContext db, TestTenantContext tenant, CountingProvider provider)
        => new(provider, db, tenant, new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance);

    [DockerFact]
    public async Task A_second_sync_over_unchanged_data_stays_within_the_estimate_and_never_reprovisions()
    {
        var tenant = TestTenantContext.New();
        var inner = new SimulatedFinancialDataProvider(Options.Create(new SimulatedProviderOptions()));

        await using var db1 = fixture.CreateDbContext(tenant);
        var first = new CountingProvider(inner);
        var firstResult = await Service(db1, tenant, first).SyncAsync();
        firstResult.TransactionsAdded.Should().BeGreaterThan(0);

        await using var db2 = fixture.CreateDbContext(tenant);
        var second = new CountingProvider(inner);
        var secondResult = await Service(db2, tenant, second).SyncAsync();

        secondResult.TransactionsAdded.Should().Be(0);
        var estimate = SyncCallEstimate.ForAccounts(secondResult.AccountsUpserted);
        second.Begin.Should().Be(0, "a second sync never provisions a new connection (billable)");
        second.Accounts.Should().Be(1, "accounts are listed once");
        second.Transactions.Should().Be(secondResult.AccountsUpserted, "an up-to-date cursor needs one page per account, no re-paging");
        (second.Accounts + second.Transactions).Should().BeLessThanOrEqualTo(estimate);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(4, 5)]
    public void Estimate_is_one_account_listing_plus_one_page_per_account(int accounts, int expected)
        => SyncCallEstimate.ForAccounts(accounts).Should().Be(expected);
}
