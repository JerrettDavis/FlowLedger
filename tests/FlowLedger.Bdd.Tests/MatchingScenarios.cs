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
/// Planned-vs-actual matching scenarios driven through the real <see cref="MatchingEngine"/>,
/// real EF repositories, and Testcontainers Postgres.
/// </summary>
[Feature("Planned-vs-actual matching")]
[Collection(BddIntegrationCollection.Name)]
public partial class MatchingScenarios : TinyBddXunitBase
{
    private readonly BddTestFixture _fixture;

    public MatchingScenarios(BddTestFixture fixture, ITestOutputHelper output) : base(output)
        => _fixture = fixture;

    // ── Scenario 2 ────────────────────────────────────────────────────────────

    // [DisableOptimization]: routes through the runtime ambient path so local-function step
    // delegates resolve correctly (the source generator cannot see local functions).
    [Scenario("Match an actual transaction to a planned bill"), DockerFact, DisableOptimization]
    public async Task Match_actual_transaction_to_planned_bill()
    {
        var tenant = TestTenantContext.New();
        using var scope = _fixture.CreateScope(tenant);
        var tenantId = TenantId.From(tenant.TenantId);

        var plannedDate = new DateOnly(2026, 6, 10);
        var actualDate = new DateOnly(2026, 6, 11);

        AccountId accountId = default;
        PlannedOccurrenceId occurrenceId = default;
        Transaction actual = null!;

        async Task<TestTenantContext> CleanDb()
        {
            await _fixture.ResetAsync();
            return tenant;
        }

        async Task<TestTenantContext> CreateAccount(TestTenantContext t)
        {
            var accounts = scope.Resolve<IAccountRepository>();
            var account = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(2_000m, "USD"));
            await accounts.AddAsync(account);
            await accounts.SaveChangesAsync();
            accountId = account.AccountId;
            return t;
        }

        async Task<TestTenantContext> CreatePlannedBill(TestTenantContext t)
        {
            var flows = scope.Resolve<IRecurringFlowRepository>();
            var flow = RecurringFlow.Create(
                tenantId, accountId, "Utilities", new Money(180m, "USD"),
                TransactionDirection.Debit,
                RecurrencePattern.Monthly(10),
                startDate: new DateOnly(2026, 6, 1),
                endDate: new DateOnly(2026, 12, 31));

            var occurrence = flow.GenerateOccurrence(plannedDate);
            occurrenceId = occurrence.PlannedOccurrenceId;
            await flows.AddAsync(flow);
            await flows.SaveChangesAsync();
            return t;
        }

        async Task<PlannedOccurrenceId> CreateActual(TestTenantContext _)
        {
            var txRepo = scope.Resolve<ITransactionRepository>();
            actual = Transaction.RecordActual(
                tenantId, accountId, new Money(184.25m, "USD"),
                TransactionDirection.Debit, "Utility payment",
                effectiveDate: actualDate, postedDate: actualDate,
                source: TransactionSource.CsvImport);
            await txRepo.AddAsync(actual);
            await txRepo.SaveChangesAsync();
            return occurrenceId;
        }

        async Task<PlannedOccurrenceId> RunMatch(PlannedOccurrenceId occId)
        {
            var matcher = scope.Resolve<MatchingEngine>();
            var occurrenceRepo = scope.Resolve<IPlannedOccurrenceRepository>();
            var txRepo = scope.Resolve<ITransactionRepository>();

            await matcher.MatchAsync([actual]);
            await occurrenceRepo.SaveChangesAsync();
            await txRepo.SaveChangesAsync();
            return occId;
        }

        async Task<bool> AssertMatched(PlannedOccurrenceId occId)
        {
            // Reload through a fresh scope to confirm the match persisted.
            using var verify = _fixture.CreateScope(tenant);
            var repo = verify.Resolve<IPlannedOccurrenceRepository>();
            var flow = await repo.GetOwningFlowAsync(occId);
            flow.Should().NotBeNull();

            var occurrence = flow!.Occurrences.Single(o => o.PlannedOccurrenceId == occId);
            occurrence.Status.Should().Be(OccurrenceStatus.Matched);
            occurrence.MatchConfidence.Should().NotBeNull();
            occurrence.MatchConfidence!.Value.Value.Should().BeGreaterThanOrEqualTo(0.75m);

            // Amount variance = actual 184.25 - planned 180.00 = 4.25; date variance = 1 day.
            occurrence.AmountVariance.Should().NotBeNull();
            occurrence.AmountVariance!.Amount.Should().Be(4.25m);
            occurrence.DateVarianceDays.Should().Be(1);
            return true;
        }

        await Given("a clean database", CleanDb)
            .And("a checking account", CreateAccount)
            .And("a $180 utility flow with a planned occurrence on 2026-06-10", CreatePlannedBill)
            .And("an actual $184.25 utility transaction posted on 2026-06-11", CreateActual)
            .When("the matching engine runs over the actual transaction", RunMatch)
            .Then("the occurrence is matched with confidence >= 0.75 and variance $4.25", AssertMatched)
            .AssertPassed();
    }
}
