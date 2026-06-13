using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Bdd.Tests.Support;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
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
}
