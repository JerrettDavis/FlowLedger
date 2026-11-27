using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Bdd.Tests.Support;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.TestSupport.Builders;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace FlowLedger.Bdd.Tests;

/// <summary>
/// Multi-tenant isolation scenarios driven through the real repositories, import handler and sync service, backed by
/// Testcontainers Postgres. Each tenant gets its own DI scope; the EF tenant filters are what is being exercised.
/// </summary>
[Feature("Tenant isolation")]
[Collection(BddIntegrationCollection.Name)]
public partial class TenancyScenarios : TinyBddXunitBase
{
    private const string Csv = "Date,Amount,Description\n2026-01-15,50.00,Coffee Shop";

    private readonly BddTestFixture _fixture;

    public TenancyScenarios(BddTestFixture fixture, ITestOutputHelper output) : base(output)
        => _fixture = fixture;

    // [DisableOptimization]: the TinyBDD source generator cannot see local functions used as step delegates.
    [Scenario("One tenant cannot see another tenant's accounts"), DockerFact, DisableOptimization]
    public async Task One_tenant_cannot_see_another_tenants_accounts()
    {
        var tenantA = TestTenantContext.New();
        var tenantB = TestTenantContext.New();
        AccountId accountId = default;

        async Task<bool> CleanDb()
        {
            await _fixture.ResetAsync();
            return true;
        }

        async Task<AccountId> TenantACreatesAnAccount(bool _)
        {
            using var scope = _fixture.CreateScope(tenantA);
            var repo = scope.Resolve<IAccountRepository>();
            var account = AccountBuilder.A().ForTenant(tenantA.TenantId).Named("A's checking").Holding(0m).Build();
            await repo.AddAsync(account);
            await repo.SaveChangesAsync();
            accountId = account.AccountId;
            return accountId;
        }

        async Task<(int Listed, bool Found)> TenantBLooksForIt(AccountId id)
        {
            using var scope = _fixture.CreateScope(tenantB);
            var repo = scope.Resolve<IAccountRepository>();
            return ((await repo.ListIncludingInactiveAsync()).Count, await repo.GetByIdAsync(id) is not null);
        }

        async Task<(int Listed, bool Found)> CreateThenLookup(bool _) => await TenantBLooksForIt(await TenantACreatesAnAccount(true));

        bool TenantBSeesNothing((int Listed, bool Found) seen)
        {
            seen.Listed.Should().Be(0);
            seen.Found.Should().BeFalse("a row id from another tenant resolves to nothing");
            return true;
        }

        async Task<bool> TenantAStillSeesIt((int Listed, bool Found) _)
        {
            using var scope = _fixture.CreateScope(tenantA);
            (await scope.Resolve<IAccountRepository>().GetByIdAsync(accountId)).Should().NotBeNull();
            return true;
        }

        await Given("a clean database", CleanDb)
            .When("tenant A creates an account and tenant B looks for it by listing and by id", CreateThenLookup)
            .Then("tenant B lists no accounts and cannot load it by id", TenantBSeesNothing)
            .And("tenant A still sees its account", TenantAStillSeesIt)
            .AssertPassed();
    }

    [Scenario("One tenant cannot import transactions into another tenant's account"), DockerFact, DisableOptimization]
    public async Task One_tenant_cannot_import_into_another_tenants_account()
    {
        var tenantA = TestTenantContext.New();
        var tenantB = TestTenantContext.New();
        var mapping = new CsvColumnMapping(0, 1, 2, HasHeaderRow: true);

        async Task<Guid> TenantAHasAnAccount()
        {
            await _fixture.ResetAsync();
            using var scope = _fixture.CreateScope(tenantA);
            var repo = scope.Resolve<IAccountRepository>();
            var account = AccountBuilder.A().ForTenant(tenantA.TenantId).Named("A's checking").Holding(0m).Build();
            await repo.AddAsync(account);
            await repo.SaveChangesAsync();
            return account.AccountId.Value;
        }

        async Task<(Guid Account, Exception? Error)> TenantBImportsIntoIt(Guid accountId)
        {
            using var scope = _fixture.CreateScope(tenantB);
            try
            {
                await scope.Resolve<ImportTransactionsHandler>().HandleAsync(new ImportTransactionsCommand(accountId, Csv, mapping));
                return (accountId, null);
            }
            catch (InvalidOperationException ex)
            {
                return (accountId, ex);
            }
        }

        bool TheImportIsRefused((Guid Account, Exception? Error) r)
        {
            r.Error.Should().NotBeNull("tenant B has no such account");
            r.Error!.Message.Should().Contain("not found");
            return true;
        }

        async Task<bool> NothingWasWrittenToTenantA((Guid Account, Exception? Error) r)
        {
            using var scope = _fixture.CreateScope(tenantA);
            (await scope.Resolve<ITransactionRepository>().CountAsync()).Should().Be(0);
            return true;
        }

        await Given("tenant A has an account", TenantAHasAnAccount)
            .When("tenant B imports a CSV into that account's id", TenantBImportsIntoIt)
            .Then("the import is refused as an unknown account", TheImportIsRefused)
            .And("tenant A has no transactions", NothingWasWrittenToTenantA)
            .AssertPassed();
    }

    [Scenario("Two tenants syncing the same provider each get their own accounts"), DockerFact, DisableOptimization]
    public async Task Two_tenants_syncing_get_their_own_accounts()
    {
        var tenantA = TestTenantContext.New();
        var tenantB = TestTenantContext.New();

        async Task<bool> CleanDb()
        {
            await _fixture.ResetAsync();
            return true;
        }

        async Task<(SyncResult A, SyncResult B)> BothSync(bool _)
        {
            using var a = _fixture.CreateScope(tenantA);
            var resultA = await a.Resolve<IFinancialSyncService>().SyncAsync();
            using var b = _fixture.CreateScope(tenantB);
            var resultB = await b.Resolve<IFinancialSyncService>().SyncAsync();
            return (resultA, resultB);
        }

        bool BothImportedData((SyncResult A, SyncResult B) r)
        {
            r.A.AccountsUpserted.Should().BeGreaterThan(0);
            r.B.AccountsUpserted.Should().Be(r.A.AccountsUpserted, "the Simulated provider is deterministic, so each tenant gets the same shape");
            return true;
        }

        async Task<bool> AccountSetsAreDisjoint((SyncResult A, SyncResult B) r)
        {
            using var a = _fixture.CreateScope(tenantA);
            using var b = _fixture.CreateScope(tenantB);
            var idsA = (await a.Resolve<IAccountRepository>().ListIncludingInactiveAsync()).Select(x => x.AccountId.Value).ToList();
            var idsB = (await b.Resolve<IAccountRepository>().ListIncludingInactiveAsync()).Select(x => x.AccountId.Value).ToList();
            idsA.Should().HaveCount(r.A.AccountsUpserted);
            idsB.Should().HaveCount(r.B.AccountsUpserted);
            idsA.Intersect(idsB).Should().BeEmpty("no account row may be shared between tenants");
            return true;
        }

        await Given("a clean database", CleanDb)
            .When("tenant A and tenant B each run a sync", BothSync)
            .Then("both import accounts", BothImportedData)
            .And("no account belongs to both tenants", AccountSetsAreDisjoint)
            .AssertPassed();
    }
}
