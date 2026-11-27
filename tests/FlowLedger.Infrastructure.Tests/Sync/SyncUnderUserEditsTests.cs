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

/// <summary>
/// #157: a user edit that lands between a sync's reads and its writes must neither make the sync throw nor be overwritten.
/// The edit is injected just before the sync persists a page (it runs right before the cursor save that also flushes the
/// settled rows), which is exactly the race window.
/// </summary>
[Collection("Integration")]
public sealed class SyncUnderUserEditsTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public SyncUnderUserEditsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateOnly Today = new(2026, 11, 10);

    private async Task SyncAsync(TestTenantContext tenant, ProviderTransaction[] txs, decimal balance, Func<Task>? beforePageSave = null, string providerType = "CHECKING")
    {
        await using var db = _fixture.CreateDbContext(tenant);
        var cursors = new HookedCursorStore(new EfSyncCursorStore(db, tenant), beforePageSave);
        var svc = new FinancialSyncService(new ScriptedProvider(txs, balance, providerType), db, tenant, cursors, NullLogger<FinancialSyncService>.Instance);
        await svc.SyncAsync();
    }

    private static ProviderTransaction Tx(bool pending, DateOnly date) =>
        new("t1", "acct-1", date, pending, new Money(-42.50m, new Currency("USD")), "COFFEE SHOP", "Coffee", null);

    [DockerFact]
    public async Task Settling_a_pending_row_the_user_just_edited_succeeds_and_keeps_the_edit()
    {
        var tenant = TestTenantContext.New();
        await SyncAsync(tenant, [Tx(true, Today)], 100m);

        await SyncAsync(tenant, [Tx(false, Today.AddDays(1))], 100m, beforePageSave: async () =>
        {
            await using var user = _fixture.CreateDbContext(tenant);
            var row = await user.Transactions.SingleAsync();
            row.SetNote("receipt in the drawer");
            await user.SaveChangesAsync();
        });

        await using var check = _fixture.CreateDbContext(tenant);
        var stored = await check.Transactions.SingleAsync();
        stored.Status.Should().Be(TransactionStatus.Posted, "the sync's change landed");
        stored.PostedDate.Should().Be(Today.AddDays(1));
        stored.Notes.Should().Be("receipt in the drawer", "the user's concurrent edit survived");
    }

    [DockerFact]
    public async Task A_user_renaming_the_account_mid_sync_keeps_the_name_and_still_gets_the_new_balance()
    {
        var tenant = TestTenantContext.New();
        await SyncAsync(tenant, [Tx(true, Today)], 100m);

        await SyncAsync(tenant, [Tx(true, Today)], 250m, beforePageSave: async () =>
        {
            await using var user = _fixture.CreateDbContext(tenant);
            (await user.Accounts.SingleAsync()).Rename("Household checking");
            await user.SaveChangesAsync();
        });

        await using var check = _fixture.CreateDbContext(tenant);
        var account = await check.Accounts.SingleAsync();
        account.Name.Should().Be("Household checking");
        account.CurrentBalance.Amount.Should().Be(250m);
    }

    [DockerFact]
    public async Task A_type_the_user_locks_mid_sync_is_not_overwritten_by_the_providers_reclassification()
    {
        var tenant = TestTenantContext.New();
        await SyncAsync(tenant, [Tx(true, Today)], 100m);

        await SyncAsync(tenant, [Tx(true, Today)], 100m, providerType: "SAVINGS", beforePageSave: async () =>
        {
            await using var user = _fixture.CreateDbContext(tenant);
            (await user.Accounts.SingleAsync()).SetTypeManually(AccountType.Checking);
            await user.SaveChangesAsync();
        });

        await using var check = _fixture.CreateDbContext(tenant);
        var account = await check.Accounts.SingleAsync();
        account.AccountType.Should().Be(AccountType.Checking);
        account.TypeLockedByUser.Should().BeTrue();
    }

    [DockerFact]
    public async Task A_row_deleted_mid_sync_does_not_fail_the_sync()
    {
        var tenant = TestTenantContext.New();
        await SyncAsync(tenant, [Tx(true, Today)], 100m);

        var act = () => SyncAsync(tenant, [Tx(false, Today.AddDays(1))], 100m, beforePageSave: async () =>
        {
            await using var user = _fixture.CreateDbContext(tenant);
            user.Transactions.Remove(await user.Transactions.SingleAsync());
            await user.SaveChangesAsync();
        });

        await act.Should().NotThrowAsync();
    }

    private sealed class HookedCursorStore(ISyncCursorStore inner, Func<Task>? beforeSet) : ISyncCursorStore
    {
        private bool _fired;

        public Task<string> GetAsync(string providerName, string providerAccountId, CancellationToken ct = default)
            => inner.GetAsync(providerName, providerAccountId, ct);

        public async Task SetAsync(string providerName, string providerAccountId, string cursorValue, CancellationToken ct = default)
        {
            if (beforeSet is not null && !_fired)
            {
                _fired = true;
                await beforeSet();
            }

            await inner.SetAsync(providerName, providerAccountId, cursorValue, ct);
        }

        public Task MarkSuccessfulAsync(string providerName, IReadOnlyCollection<string> providerAccountIds, CancellationToken ct = default)
            => inner.MarkSuccessfulAsync(providerName, providerAccountIds, ct);
    }

    private sealed class ScriptedProvider(ProviderTransaction[] txs, decimal balance, string providerType) : IFinancialDataProvider
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
                new ProviderAccount("acct-1", "Checking", providerType, new Money(balance, new Currency("USD")), null, "USD")]);
        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
            => Task.FromResult(new TransactionPage(txs, new SyncCursor("c"), false));
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
