using FlowLedger.Application.Features.Goals;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Goals;

public sealed class GoalMathTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);

    [Fact]
    public void Splits_remaining_over_months_until_target() =>
        GoalMath.MonthlyNeeded(1200m, new DateOnly(2027, 11, 15), Today).Should().Be(100m);

    [Fact]
    public void Past_or_current_month_target_needs_everything_now() =>
        GoalMath.MonthlyNeeded(500m, new DateOnly(2026, 10, 1), Today).Should().Be(500m);

    [Fact]
    public void Complete_or_undated_goal_has_no_monthly_need()
    {
        GoalMath.MonthlyNeeded(0m, new DateOnly(2027, 1, 1), Today).Should().BeNull();
        GoalMath.MonthlyNeeded(500m, null, Today).Should().BeNull();
    }
}
