using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>ADR 0010: several connectors enabled at once, each account linked to one of them.</summary>
[Collection("Integration")]
public sealed class MultiConnectorSyncTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly List<Guid> _tenants = [];

    public MultiConnectorSyncTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM account_connector_links WHERE tenant_id = ANY(@t)", conn);
        cmd.Parameters.AddWithValue("t", _tenants.ToArray());
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // table never created
        }
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>Provider that reports a fixed account list, has no transactions, and can be told to fail. Uses real provider names so
    /// the sync service provisions its (local) connection record exactly like SimpleFIN / Simulated.</summary>
    private sealed class StubProvider(string name, string[] providerAccountIds, Exception? failure = null) : IFinancialDataProvider
    {
        public string ProviderName => name;
        public ProviderCapabilities Capabilities => ProviderCapabilities.Accounts;
        public ConnectorTraits Traits => name == "Simulated"
            ? ConnectorTraits.PerTenantAccess | ConnectorTraits.ProvisionsLocally | ConnectorTraits.SeedsDemoData
            : ConnectorTraits.ProvisionsLocally | ConnectorTraits.ReportsHoldingsSign;
        public Task<ProviderMemberRef> BeginConnectionAsync(TenantId t, CancellationToken ct = default)
            => Task.FromResult(new ProviderMemberRef("m-" + name, name + " bank", ConnectionStatus.Connected));
        public Task<ConnectionStatus> GetConnectionStatusAsync(string m, CancellationToken ct = default)
            => Task.FromResult(ConnectionStatus.Connected);
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(string m, CancellationToken ct = default)
            => failure is not null
                ? throw failure
                : Task.FromResult<IReadOnlyList<ProviderAccount>>(providerAccountIds
                    .Select(id => new ProviderAccount(id, "Acct " + id, "CHECKING", new Money(100m, new Currency("USD")), null, "USD"))
                    .ToList());
        public Task<TransactionPage> GetTransactionsAsync(string a, SyncCursor c, int p = 100, CancellationToken ct = default)
            => Task.FromResult(new TransactionPage([], new SyncCursor("c"), false));
        public Task VerifyWebhookAsync(ReadOnlyMemory<byte> b, string s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProviderWebhookEvent> ParseWebhookAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class TestRegistry(params (ConnectorDescriptor Descriptor, IFinancialDataProvider Provider)[] connectors) : IConnectorRegistry
    {
        public IReadOnlyList<ConnectorDescriptor> Enabled { get; } = connectors.Select(c => c.Descriptor).ToList();
        public bool IsEnabled(ConnectorId id) => Enabled.Any(c => c.Id == id);
        public IFinancialDataProvider Resolve(ConnectorId id) => connectors.First(c => c.Descriptor.Id == id).Provider;
    }

    private static (ConnectorDescriptor, IFinancialDataProvider) Connector(string name, string[] ids, Exception? failure = null, bool shared = true)
        => (new ConnectorDescriptor(name, name, "Test", shared), new StubProvider(name, ids, failure));

    private sealed record Sut(MultiConnectorSyncService Service, FlowLedgerDbContext Db, IAccountConnectorLinkStore Links, TestTenantContext Tenant);

    private Sut Build(Guid householdTenant, TestTenantContext tenant, params (ConnectorDescriptor, IFinancialDataProvider)[] connectors)
        => BuildWith(householdTenant, tenant, production: false, decorate: null, connectors);

    private Sut BuildWith(
        Guid householdTenant, TestTenantContext tenant, bool production, Func<IAccountConnectorLinkStore, IAccountConnectorLinkStore>? decorate,
        params (ConnectorDescriptor, IFinancialDataProvider)[] connectors)
    {
        _tenants.Add(tenant.TenantId);
        var db = production ? _fixture.CreateProductionStyleContext(tenant) : _fixture.CreateDbContext(tenant);
        var realLinks = new FlowLedger.Infrastructure.Persistence.Repositories.EfAccountConnectorLinkStore(db, tenant);
        IAccountConnectorLinkStore links = decorate is null ? realLinks : decorate(realLinks);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tenancy:LegacyTenantId"] = householdTenant.ToString() })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITenantContext>(tenant)
            .AddSingleton(db)
            .AddSingleton<ISyncCursorStore>(new EfSyncCursorStore(db, tenant))
            .AddSingleton<IAccountConnectorLinkStore>(links)
            .AddSingleton<IConfiguration>(cfg)
            .BuildServiceProvider();
        var registry = new TestRegistry(connectors);
        var svc = new MultiConnectorSyncService(services, registry, tenant, cfg, NullLogger<MultiConnectorSyncService>.Instance);
        return new Sut(svc, db, links, tenant);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Each_connector_creates_and_owns_its_accounts()
    {
        var tenant = TestTenantContext.New();
        var sut = Build(tenant.TenantId, tenant, Connector("SimpleFIN", ["sf-1", "sf-2"]), Connector("Simulated", ["mx-1"]));

        var result = await sut.Service.SyncAsync();

        result.AccountsUpserted.Should().Be(3);
        var links = await sut.Links.ListAsync();
        links.Should().HaveCount(3);
        links.Where(l => l.ConnectorId == "SimpleFIN").Select(l => l.ProviderAccountId).Should().BeEquivalentTo("sf-1", "sf-2");
        links.Single(l => l.ConnectorId == "Simulated").ProviderAccountId.Should().Be("mx-1");
        (await _fixture.CreateDbContext(tenant).Accounts.CountAsync()).Should().Be(3);
    }

    [DockerFact]
    public async Task The_same_provider_account_id_under_two_connectors_does_not_duplicate_the_account()
    {
        var tenant = TestTenantContext.New();
        var sut = Build(tenant.TenantId, tenant, Connector("SimpleFIN", ["shared-1"]), Connector("Simulated", ["shared-1"]));

        var result = await sut.Service.SyncAsync();

        // The first connector claims the account; the second finds it linked elsewhere and skips it.
        result.AccountsUpserted.Should().Be(1);
        (await sut.Links.ListAsync()).Should().ContainSingle().Which.ConnectorId.Value.Should().Be("SimpleFIN");
        (await _fixture.CreateDbContext(tenant).Accounts.CountAsync()).Should().Be(1);
    }

    [DockerFact]
    public async Task A_legacy_account_without_a_link_is_matched_and_linked_on_sync()
    {
        var tenant = TestTenantContext.New();
        var sut = Build(tenant.TenantId, tenant, Connector("SimpleFIN", ["legacy-1"]));
        var legacy = Account.Create(TenantId.From(tenant.TenantId), "Old", AccountType.Checking, new Money(5m, new Currency("USD")), FlowLedger.TestSupport.TestClock.Provider, null, "legacy-1");
        sut.Db.Accounts.Add(legacy);
        await sut.Db.SaveChangesAsync();

        await sut.Service.SyncAsync();

        (await sut.Links.FindByAccountAsync(legacy.Id))!.ConnectorId.Value.Should().Be("SimpleFIN");
        (await _fixture.CreateDbContext(tenant).Accounts.CountAsync()).Should().Be(1, "the existing account is reused, not duplicated");
    }

    [DockerFact]
    public async Task An_account_pinned_manual_is_never_synced()
    {
        var tenant = TestTenantContext.New();
        var sut = Build(tenant.TenantId, tenant, Connector("SimpleFIN", ["pin-1"]));
        var acct = Account.Create(TenantId.From(tenant.TenantId), "Mine", AccountType.Checking, new Money(5m, new Currency("USD")), FlowLedger.TestSupport.TestClock.Provider, null, "pin-1");
        sut.Db.Accounts.Add(acct);
        await sut.Db.SaveChangesAsync();
        await sut.Links.PinManualAsync(acct.Id);

        var result = await sut.Service.SyncAsync();

        result.AccountsUpserted.Should().Be(0);
        (await _fixture.CreateDbContext(tenant).Accounts.SingleAsync()).CurrentBalance.Amount.Should().Be(5m, "a pinned account keeps its balance");
    }

    [DockerFact]
    public async Task One_failing_connector_does_not_stop_the_others_but_all_failing_rethrows()
    {
        var tenant = TestTenantContext.New();
        var boom = new InvalidOperationException("boom");

        var partial = Build(tenant.TenantId, tenant, Connector("SimpleFIN", [], failure: boom), Connector("Simulated", ["ok-1"]));
        (await partial.Service.SyncAsync()).AccountsUpserted.Should().Be(1);

        var tenant2 = TestTenantContext.New();
        var allFail = Build(tenant2.TenantId, tenant2, Connector("SimpleFIN", [], failure: boom), Connector("Simulated", [], failure: new InvalidOperationException("second")));
        var act = () => allFail.Service.SyncAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("boom");
    }

    [DockerFact]
    public async Task Shared_credential_connectors_serve_only_the_household_tenant()
    {
        var household = Guid.NewGuid();
        var other = TestTenantContext.New();
        var sut = Build(household, other, Connector("SimpleFIN", ["h-1"]));

        (await sut.Service.SyncAsync()).Should().Be(SyncResultBuilder.A().Build());
        (await sut.Links.ListAsync()).Should().BeEmpty();

        var viaConnect = () => sut.Service.ConnectAsync();
        await viaConnect.Should().ThrowAsync<NeedsUserActionProviderException>();
    }

    /// <summary>Delegates, then throws once the link row exists: the fault the atomicity guarantee is about (#180).</summary>
    private sealed class ThrowAfterLink(IAccountConnectorLinkStore inner) : IAccountConnectorLinkStore
    {
        public Task<IReadOnlyList<AccountConnectorLink>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<AccountConnectorLink?> FindByProviderAccountAsync(ConnectorId c, string p, CancellationToken ct = default) => inner.FindByProviderAccountAsync(c, p, ct);
        public Task<AccountConnectorLink?> FindByAccountAsync(Guid a, CancellationToken ct = default) => inner.FindByAccountAsync(a, ct);
        public Task PinManualAsync(Guid a, CancellationToken ct = default) => inner.PinManualAsync(a, ct);
        public Task<bool> UnlinkAsync(Guid a, CancellationToken ct = default) => inner.UnlinkAsync(a, ct);

        public async Task<bool> LinkAsync(Guid accountId, ConnectorId connectorId, string providerAccountId, CancellationToken ct = default)
        {
            await inner.LinkAsync(accountId, connectorId, providerAccountId, ct);
            throw new InvalidOperationException("injected fault after the link was created");
        }
    }

    [DockerFact]
    public async Task A_failure_after_the_link_is_created_rolls_back_the_account_and_the_link_together()
    {
        var tenant = TestTenantContext.New();
        var sut = BuildWith(tenant.TenantId, tenant, production: true, decorate: l => new ThrowAfterLink(l), Connector("SimpleFIN", ["fault-1"]));

        var act = () => sut.Service.SyncAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*injected fault*");

        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Accounts.CountAsync()).Should().Be(0, "the account was created in the same transaction as the failed link");
        (await verify.AccountConnectorLinks.CountAsync()).Should().Be(0, "no orphan link survives");
    }

    [DockerFact]
    public async Task Account_and_link_commit_together_under_the_production_retrying_execution_strategy()
    {
        var tenant = TestTenantContext.New();
        var sut = BuildWith(tenant.TenantId, tenant, production: true, decorate: null, Connector("SimpleFIN", ["ok-1", "ok-2"]));

        (await sut.Service.SyncAsync()).AccountsUpserted.Should().Be(2);

        await using var verify = _fixture.CreateDbContext(tenant);
        (await verify.Accounts.CountAsync()).Should().Be(2);
        (await verify.AccountConnectorLinks.CountAsync()).Should().Be(2);
    }
}
