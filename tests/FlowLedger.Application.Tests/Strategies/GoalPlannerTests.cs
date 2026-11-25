using FlowLedger.Application.Features.Strategies;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Strategies;

public class GoalPlannerTests
{
    private static readonly MarketRates Rates = MarketRates.Defaults(new DateOnly(2026, 11, 24));

    private static GoalRequest Req(decimal target = 12000, decimal saved = 0, decimal monthly = 1000, decimal apy = 0, int deadline = 0,
        decimal extra = 0, decimal raise = 0, decimal cut = 0, decimal lump = 0, decimal scApy = 0) =>
        new(target, saved, monthly, apy, deadline, extra, raise, cut, lump, scApy);

    private static readonly GoalContext Ctx = new(5000m, 4000m, []);

    [Fact]
    public void Zero_rate_months_is_simple_division()
    {
        GoalPlanner.MonthsToReach(12000, 0, 1000, 0).Should().Be(12);
    }

    [Fact]
    public void Already_there_is_zero_and_unreachable_is_null()
    {
        GoalPlanner.MonthsToReach(100, 500, 0, 0).Should().Be(0);
        GoalPlanner.MonthsToReach(100000, 0, 0, 0).Should().BeNull();
    }

    [Fact]
    public void Required_monthly_roundtrips_with_months_to_reach()
    {
        var need = GoalPlanner.RequiredMonthly(50000, 5000, 4m, 60);
        GoalPlanner.MonthsToReach(50000, 5000, need, 4m).Should().BeLessThanOrEqualTo(60);
        GoalPlanner.MonthsToReach(50000, 5000, need - 50, 4m).Should().BeGreaterThan(60);
    }

    [Fact]
    public void Scenario_levers_make_the_goal_sooner()
    {
        var plan = GoalPlanner.Plan(Req(target: 24000, extra: 500, lump: 2000), Ctx, Rates);
        plan.ScenarioMonths.Should().BeLessThan(plan.BaselineMonths!.Value);
        plan.MonthsSaved.Should().BeGreaterThan(0);
        plan.ScenarioMonthly.Should().Be(1500);
        plan.Scenario.Count.Should().Be(plan.Baseline.Count);
    }

    [Fact]
    public void Deadline_shortfall_is_called_out_and_levers_are_ranked()
    {
        var plan = GoalPlanner.Plan(Req(target: 60000, monthly: 500, deadline: 24), Ctx, Rates);
        plan.RequiredMonthly.Should().BeGreaterThan(2000);
        plan.Shortfall.Should().BeGreaterThan(0);
        plan.Callouts.Should().Contain(c => c.Contains("more income"));
        plan.Levers.Should().NotBeEmpty();
        plan.Levers.Select(l => l.MonthsSaved ?? int.MinValue).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Expensive_debt_is_flagged_and_offered_as_a_lever()
    {
        var ctx = new GoalContext(5000m, 4200m, [new GoalDebt("Visa", 6000m, 24m, 36)]);
        var plan = GoalPlanner.Plan(Req(target: 30000, monthly: 400, apy: 1m), ctx, Rates);
        plan.Levers.Should().Contain(l => l.Id == "debt-Visa");
        plan.Callouts.Should().Contain(c => c.Contains("Visa") && c.Contains("24"));
    }
}
