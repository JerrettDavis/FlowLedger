using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Bdd.Tests.Support;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace FlowLedger.Bdd.Tests;

/// <summary>
/// Forecast behaviour scenarios driven through the real <see cref="GetForecastHandler"/>,
/// the real EF repositories, and a real <see cref="ForecastEngine"/> against Testcontainers Postgres.
/// </summary>
[Feature("Cash-flow forecasting")]
[Collection(BddIntegrationCollection.Name)]
public partial class ForecastScenarios : TinyBddXunitBase
{
    private readonly BddTestFixture _fixture;

    public ForecastScenarios(BddTestFixture fixture, ITestOutputHelper output) : base(output)
        => _fixture = fixture;

    // ── Scenario 1 ────────────────────────────────────────────────────────────

    // [DisableOptimization]: the TinyBDD source generator inlines lambda bodies into a generated
    // method and cannot resolve local functions / method groups used as step delegates. Disabling
    // the optimizer routes these scenarios through the runtime ambient path, which handles them.
    [Scenario("Forecast includes recurring payroll and bills"), DockerFact, DisableOptimization]
    public async Task Forecast_includes_recurring_payroll_and_bills()
    {
        var tenant = TestTenantContext.New();
        using var scope = _fixture.CreateScope(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        async Task<TestTenantContext> CleanDb()
        {
            await _fixture.ResetAsync();
            return tenant;
        }

        async Task<AccountId> CreateAccount(TestTenantContext _)
        {
            var accounts = scope.Resolve<IAccountRepository>();
            var account = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(1_000m, "USD"));
            await accounts.AddAsync(account);
            await accounts.SaveChangesAsync();
            return account.AccountId;
        }

        async Task<AccountId> CreateFlows(AccountId accountId)
        {
            var flows = scope.Resolve<IRecurringFlowRepository>();

            var payroll = RecurringFlow.Create(
                tenantId, accountId, "Payroll", new Money(2_000m, "USD"),
                TransactionDirection.Credit,
                RecurrencePattern.EveryNWeeks(2, today.DayOfWeek),
                startDate: today);
            await flows.AddAsync(payroll);

            var rent = RecurringFlow.Create(
                tenantId, accountId, "Rent", new Money(1_500m, "USD"),
                TransactionDirection.Debit,
                RecurrencePattern.Monthly(1),
                startDate: today);
            await flows.AddAsync(rent);

            await flows.SaveChangesAsync();
            return accountId;
        }

        async Task<(AccountId, ForecastResult)> RunForecast(AccountId accountId)
        {
            var handler = scope.Resolve<GetForecastHandler>();
            var result = await handler.HandleAsync(new GetForecastQuery
            {
                AsOf = today,
                HorizonStart = today,
                HorizonEnd = today.AddDays(45),
            });
            return (accountId, result);
        }

        bool HasPoints((AccountId AccountId, ForecastResult Result) x)
        {
            var series = x.Result.AccountSeries.Single(s => s.AccountId == x.AccountId);
            series.Points.Should().NotBeEmpty();
            series.StartingBalance.Amount.Should().Be(1_000m);
            return true;
        }

        bool BalanceChanges((AccountId AccountId, ForecastResult Result) x)
        {
            var series = x.Result.AccountSeries.Single(s => s.AccountId == x.AccountId);
            var balances = series.Points.Select(p => p.Balance.Amount).Distinct().ToList();
            balances.Count.Should().BeGreaterThan(1,
                "payroll credits and the rent debit move the projected balance");
            return true;
        }

        bool LowWaterMarkPopulated((AccountId AccountId, ForecastResult Result) x)
        {
            var lwm = x.Result.LowWaterMarks.Single(m => m.AccountId == x.AccountId);
            lwm.Date.Should().BeOnOrAfter(today);
            x.Result.AggregateLowWaterMark.Should().NotBeNull();
            return true;
        }

        await Given("a clean database", CleanDb)
            .And("a checking account starting at $1,000", CreateAccount)
            .And("a $2,000 payroll credit every 2 weeks and a $1,500 rent debit on the 1st", CreateFlows)
            .When("the forecast runs over the next 45 days", RunForecast)
            .Then("the account series has projected points", HasPoints)
            .And("the balance changes over the horizon", BalanceChanges)
            .And("the low-water mark is populated", LowWaterMarkPopulated)
            .AssertPassed();
    }

    // ── Scenario 6 ────────────────────────────────────────────────────────────

    // TODO (Phase 6 deferral): A first-class LowBalanceWarning IDomainEvent + handler was
    // intentionally deferred. The forecast engine is a pure, side-effect-free read model — raising
    // a domain event from it would violate that contract and would require a new src domain event
    // (out of scope for this phase). The overdraft / low-water-mark output already provides the
    // low-balance signal, so this scenario asserts the BEHAVIOUR (OverdraftWarnings populated OR
    // AggregateLowWaterMark below zero) rather than an event. See ADR-0002.
    [Scenario("Forecast surfaces a low-water-mark / overdraft signal"), DockerFact, DisableOptimization]
    public async Task Forecast_surfaces_low_water_mark_signal()
    {
        var tenant = TestTenantContext.New();
        using var scope = _fixture.CreateScope(tenant);
        var tenantId = TenantId.From(tenant.TenantId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        async Task<TestTenantContext> CleanDb()
        {
            await _fixture.ResetAsync();
            return tenant;
        }

        async Task<AccountId> CreateAccount(TestTenantContext _)
        {
            var accounts = scope.Resolve<IAccountRepository>();
            var account = Account.Create(tenantId, "Checking", AccountType.Checking, new Money(500m, "USD"));
            await accounts.AddAsync(account);
            await accounts.SaveChangesAsync();
            return account.AccountId;
        }

        async Task<AccountId> CreateBigDebit(AccountId accountId)
        {
            var flows = scope.Resolve<IRecurringFlowRepository>();
            var bigDebit = RecurringFlow.Create(
                tenantId, accountId, "Big Loan Payment", new Money(3_000m, "USD"),
                TransactionDirection.Debit,
                RecurrencePattern.Monthly(today.AddDays(5).Day),
                startDate: today);
            await flows.AddAsync(bigDebit);
            await flows.SaveChangesAsync();
            return accountId;
        }

        async Task<(AccountId, ForecastResult)> RunForecast(AccountId accountId)
        {
            var handler = scope.Resolve<GetForecastHandler>();
            var result = await handler.HandleAsync(new GetForecastQuery
            {
                AsOf = today,
                HorizonStart = today,
                HorizonEnd = today.AddDays(45),
            });
            return (accountId, result);
        }

        bool LowBalanceSignalled((AccountId AccountId, ForecastResult Result) x)
        {
            var overdraftRaised = x.Result.OverdraftWarnings.Any(w => w.AccountId == x.AccountId);
            var aggregateNegative = x.Result.AggregateLowWaterMark.MinBalance.Amount < 0m;
            (overdraftRaised || aggregateNegative).Should().BeTrue(
                "a $3,000 debit on a $500 balance must surface a low-balance signal");
            return true;
        }

        await Given("a clean database", CleanDb)
            .And("a checking account starting at $500", CreateAccount)
            .And("a large $3,000 monthly debit that drives the balance below zero", CreateBigDebit)
            .When("the forecast runs over the next 45 days", RunForecast)
            .Then("an overdraft warning is raised or the aggregate low-water mark is negative", LowBalanceSignalled)
            .AssertPassed();
    }
}
