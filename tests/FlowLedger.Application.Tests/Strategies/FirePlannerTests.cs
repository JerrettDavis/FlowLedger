using FlowLedger.Application.Features.Strategies;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Strategies;

public sealed class FirePlannerTests
{
    [Fact]
    public void FireNumber_IsSpendDividedByWithdrawalRate()
    {
        var r = FirePlanner.Plan(0, 0, 40_000m, 4m, 5m, null, 10);
        r.FireNumber.Should().Be(1_000_000m);
        r.Reachable.Should().BeFalse();
        r.MonthsToFire.Should().BeNull();
    }

    [Fact]
    public void AlreadyFinanciallyIndependent_ReachesAtMonthZero()
    {
        var r = FirePlanner.Plan(1_200_000m, 0, 40_000m, 4m, 5m, 50m, 5);
        r.MonthsToFire.Should().Be(0);
        r.FireAge.Should().Be(50m);
    }

    [Fact]
    public void Contributions_ReachFireAndReportAge()
    {
        var r = FirePlanner.Plan(100_000m, 3_000m, 40_000m, 4m, 5m, 35m, 40);
        r.Reachable.Should().BeTrue();
        r.MonthsToFire.Should().BeInRange(150, 250);
        r.FireAge.Should().BeGreaterThan(35m);
        r.ProjectedAnnualIncome.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void ZeroWithdrawalRate_Throws()
    {
        var act = () => FirePlanner.Plan(0, 0, 1000m, 0m, 5m, null, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
