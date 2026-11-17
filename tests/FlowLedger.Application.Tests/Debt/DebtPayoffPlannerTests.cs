using FlowLedger.Application.Features.Debt;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Debt;

public sealed class DebtPayoffPlannerTests
{
    private static readonly DebtInput[] Debts =
    [
        new("Card", 1000m, 24m, 25m),
        new("Loan", 500m, 6m, 25m),
    ];

    [Fact]
    public void Zero_interest_single_debt_pays_off_in_balance_over_payment_months()
    {
        var plan = DebtPayoffPlanner.Plan([new("Zero", 1000m, 0m, 100m)], 0m, DebtPayoffPlanner.Avalanche);

        plan.Feasible.Should().BeTrue();
        plan.Months.Should().Be(10);
        plan.TotalInterest.Should().Be(0m);
        plan.TotalPaid.Should().Be(1000m);
    }

    [Fact]
    public void Avalanche_pays_less_interest_than_snowball_when_high_rate_debt_is_larger()
    {
        var avalanche = DebtPayoffPlanner.Plan(Debts, 100m, DebtPayoffPlanner.Avalanche);
        var snowball = DebtPayoffPlanner.Plan(Debts, 100m, DebtPayoffPlanner.Snowball);

        avalanche.Feasible.Should().BeTrue();
        snowball.Feasible.Should().BeTrue();
        avalanche.TotalInterest.Should().BeLessThan(snowball.TotalInterest);
    }

    [Fact]
    public void Extra_payment_shortens_payoff()
    {
        var slow = DebtPayoffPlanner.Plan(Debts, 0m, DebtPayoffPlanner.Avalanche);
        var fast = DebtPayoffPlanner.Plan(Debts, 200m, DebtPayoffPlanner.Avalanche);

        fast.Months.Should().BeLessThan(slow.Months);
        fast.TotalInterest.Should().BeLessThan(slow.TotalInterest);
    }

    [Fact]
    public void Payment_below_interest_is_reported_infeasible()
    {
        var plan = DebtPayoffPlanner.Plan([new("Hole", 10000m, 30m, 50m)], 0m, DebtPayoffPlanner.Avalanche);

        plan.Feasible.Should().BeFalse();
        plan.Debts.Single().PayoffMonth.Should().Be(-1);
    }

    [Fact]
    public void Total_paid_equals_principal_plus_interest()
    {
        var plan = DebtPayoffPlanner.Plan(Debts, 50m, DebtPayoffPlanner.Snowball);

        plan.TotalPaid.Should().Be(1500m + plan.TotalInterest);
    }
}
