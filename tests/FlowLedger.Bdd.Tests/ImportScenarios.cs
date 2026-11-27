using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Bdd.Tests.Support;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.TestSupport.Builders;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace FlowLedger.Bdd.Tests;

/// <summary>
/// CSV import + dedup scenarios driven through the real <see cref="ImportTransactionsHandler"/>,
/// real EF repositories, and Testcontainers Postgres.
/// </summary>
[Feature("CSV transaction import")]
[Collection(BddIntegrationCollection.Name)]
public partial class ImportScenarios : TinyBddXunitBase
{
    private const string Csv =
        "Date,Amount,Description\n" +
        "2026-01-15,50.00,Coffee Shop\n" +
        "2026-01-16,100.00,Grocery Store";

    private static readonly CsvColumnMapping Mapping = new(
        DateColumnIndex: 0,
        AmountColumnIndex: 1,
        DescriptionColumnIndex: 2,
        HasHeaderRow: true);

    private readonly BddTestFixture _fixture;

    public ImportScenarios(BddTestFixture fixture, ITestOutputHelper output) : base(output)
        => _fixture = fixture;

    // ── Scenario 3 ────────────────────────────────────────────────────────────

    // [DisableOptimization]: the TinyBDD source generator inlines local function bodies into a
    // generated method where they are out of scope. Disabling the optimizer routes this scenario
    // through the runtime ambient path, which resolves local functions and closures correctly.
    [Scenario("CSV import dedupes a re-imported file"), DockerFact, DisableOptimization]
    public async Task Csv_import_dedupes_reimport()
    {
        var tenant = TestTenantContext.New();
        using var scope = _fixture.CreateScope(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        Guid accountId = default;

        async Task<TestTenantContext> CleanDb()
        {
            await _fixture.ResetAsync();
            return tenant;
        }

        async Task<ImportSummaryDto> CreateAccountAndImport(TestTenantContext _)
        {
            var accounts = scope.Resolve<IAccountRepository>();
            var account = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(0m, "USD"));
            await accounts.AddAsync(account);
            await accounts.SaveChangesAsync();
            accountId = account.AccountId.Value;

            var handler = scope.Resolve<ImportTransactionsHandler>();
            return await handler.HandleAsync(new ImportTransactionsCommand(accountId, Csv, Mapping));
        }

        bool AssertImportedTwo(ImportSummaryDto first)
        {
            first.ImportedCount.Should().Be(2);
            first.DuplicateCount.Should().Be(0);
            first.FailedRowCount.Should().Be(0);
            return true;
        }

        async Task<bool> ReimportDedupes(ImportSummaryDto _)
        {
            // Fresh scope so the second import runs against persisted state, not a change-tracker
            // that already holds the inserted rows.
            using var secondScope = _fixture.CreateScope(tenant);
            var handler = secondScope.Resolve<ImportTransactionsHandler>();
            var second = await handler.HandleAsync(new ImportTransactionsCommand(accountId, Csv, Mapping));

            second.ImportedCount.Should().Be(0);
            second.DuplicateCount.Should().Be(2);
            return true;
        }

        await Given("a clean database", CleanDb)
            .When("an account is created and the CSV with two transactions is imported", CreateAccountAndImport)
            .Then("two transactions are imported with no duplicates or errors", AssertImportedTwo)
            .And("re-importing the same CSV imports nothing and flags both as duplicates", ReimportDedupes)
            .AssertPassed();
    }

    [Scenario("CSV import reports malformed rows without losing the good ones"), DockerFact, DisableOptimization]
    public async Task Csv_import_reports_bad_rows_and_keeps_good_ones()
    {
        var tenant = TestTenantContext.New();
        using var scope = _fixture.CreateScope(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        const string mixed =
            "Date,Amount,Description\n" +
            "2026-01-15,50.00,Coffee Shop\n" +
            "not-a-date,10.00,Broken Date\n" +
            "2026-01-17,12.34,Bakery";

        async Task<Guid> CleanDbWithAccount()
        {
            await _fixture.ResetAsync();
            var accounts = scope.Resolve<IAccountRepository>();
            var account = AccountBuilder.A().ForTenant(tenantId).Named("Checking").Holding(0m).Build();
            await accounts.AddAsync(account);
            await accounts.SaveChangesAsync();
            return account.AccountId.Value;
        }

        async Task<ImportSummaryDto> ImportMixedFile(Guid accountId)
            => await scope.Resolve<ImportTransactionsHandler>().HandleAsync(new ImportTransactionsCommand(accountId, mixed, Mapping));

        bool GoodRowsImportedAndBadRowReported(ImportSummaryDto summary)
        {
            summary.ImportedCount.Should().Be(2);
            summary.FailedRowCount.Should().Be(1);
            summary.RowErrors.Should().ContainSingle();
            return true;
        }

        async Task<bool> OnlyGoodRowsArePersisted(ImportSummaryDto _)
        {
            using var check = _fixture.CreateScope(tenant);
            (await check.Resolve<ITransactionRepository>().CountAsync()).Should().Be(2);
            return true;
        }

        await Given("an account in a clean database", CleanDbWithAccount)
            .When("a CSV with two good rows and one unparseable date is imported", ImportMixedFile)
            .Then("two rows are imported and one is reported as failed", GoodRowsImportedAndBadRowReported)
            .And("only the two good rows are stored", OnlyGoodRowsArePersisted)
            .AssertPassed();
    }

    [Scenario("CSV import keeps genuine same-day repeats but still dedupes a re-import"), DockerFact, DisableOptimization]
    public async Task Csv_import_keeps_same_day_repeats_and_dedupes_reimport()
    {
        var tenant = TestTenantContext.New();
        using var scope = _fixture.CreateScope(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        var nl = (char)10;
        var twice = $"Date,Amount,Description{nl}2026-02-01,9.99,Transit Fare{nl}2026-02-01,9.99,Transit Fare";

        async Task<Guid> CleanDbWithAccount()
        {
            await _fixture.ResetAsync();
            var accounts = scope.Resolve<IAccountRepository>();
            var account = AccountBuilder.A().ForTenant(tenantId).Named("Checking").Holding(0m).Build();
            await accounts.AddAsync(account);
            await accounts.SaveChangesAsync();
            return account.AccountId.Value;
        }

        async Task<(Guid Account, ImportSummaryDto First)> ImportRepeatedRow(Guid accountId)
            => (accountId, await scope.Resolve<ImportTransactionsHandler>().HandleAsync(new ImportTransactionsCommand(accountId, twice, Mapping)));

        bool BothFaresImported((Guid Account, ImportSummaryDto First) r)
        {
            r.First.ImportedCount.Should().Be(2, "two identical same-day rows are two real purchases, not a duplicate");
            r.First.DuplicateCount.Should().Be(0);
            return true;
        }

        async Task<bool> ReimportIsAllDuplicates((Guid Account, ImportSummaryDto First) r)
        {
            using var second = _fixture.CreateScope(tenant);
            var again = await second.Resolve<ImportTransactionsHandler>().HandleAsync(new ImportTransactionsCommand(r.Account, twice, Mapping));
            again.ImportedCount.Should().Be(0);
            again.DuplicateCount.Should().Be(2);
            return true;
        }

        await Given("an account in a clean database", CleanDbWithAccount)
            .When("a CSV containing the same row twice is imported", ImportRepeatedRow)
            .Then("both rows are kept as separate transactions", BothFaresImported)
            .And("importing the same file again flags both as duplicates", ReimportIsAllDuplicates)
            .AssertPassed();
    }
}
