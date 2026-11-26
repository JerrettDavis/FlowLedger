using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>A provider-reported balance is a fact: an overdrawn checking account must not keep a stale positive balance.</summary>
[Collection("Integration")]
public sealed class ProviderBalanceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public ProviderBalanceTests(IntegrationTestFixture fixture) => _fixture = fixture;
    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SyncAsync(TestTenantContext tenant, decimal balance, string currency = "USD")
    {
        await using var db = _fixture.CreateDbContext(tenant);
        var svc = new FinancialSyncService(new BalanceProvider(balance, currency), db, tenant,
            new EfSyncCursorStore(db, tenant), NullLogger<FinancialSyncService>.Instance);
        await svc.SyncAsync();
    }

    [DockerFact]
    public async Task Overdrawn_checking_account_takes_the_negative_provider_balance()
    {
        var tenant = TestTenantContext.New();
        await SyncAsync(tenant, 120m);
        await SyncAsync(tenant, -35m);

        await using var v = _fixture.CreateDbContext(tenant);
        (await v.Accounts.SingleAsync()).CurrentBalance.Amount.Should().Be(-35m);
        (await v.BalanceSnapshots.OrderBy(s => s.CapturedAt).LastAsync()).Balance.Should().Be(-35m);

        await SyncAsync(tenant, -50m);
        await using var v2 = _fixture.CreateDbContext(tenant);
        (await v2.Accounts.SingleAsync()).CurrentBalance.Amount.Should().Be(-50m);
    }

    [DockerFact]
    public async Task Provider_currency_change_does_not_abort_the_sync()
    {
        var tenant = TestTenantContext.New();
        await SyncAsync(tenant, 120m);
        var act = () => SyncAsync(tenant, 80m, "EUR");
        await act.Should().NotThrowAsync();
    }

    private sealed class BalanceProvider(decimal balance, string currency) : IFinancialDataProvider
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
                    new Money(balance, new Currency(currency)), null, currency)]);
        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
            => Task.FromResult(new TransactionPage([], new SyncCursor("c"), false));
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
