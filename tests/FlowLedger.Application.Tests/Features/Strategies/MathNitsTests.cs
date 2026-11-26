using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Application.Features.Goals;
using FlowLedger.Application.Features.Strategies;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Strategies;

/// <summary>Regression tests for #129 (goal affordability, horizon clamp, APY compounding, shared rounding).</summary>
public sealed class MathNitsTests
{
    private static readonly Currency Usd = new("USD");

    [Fact]
    public void Goal_is_affordable_immediately_when_linked_balance_already_covers_remaining_and_no_events_exist()
    {
        var account = AccountId.New();
        var asOf = new DateOnly(2025, 1, 1);
        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, asOf.AddMonths(3)),
            StartingBalances = new Dictionary<AccountId, Money> { [account] = new Money(10000m, Usd) },
            AccountIds = [account],
            RecurringFlows = [],
            Goals =
            [
                new GoalAffordabilityInput
                {
                    GoalId = Guid.NewGuid(), Name = "Trip", TargetAmount = new Money(5000m, Usd),
                    CurrentBalance = new Money(0m, Usd), LinkedAccountId = account
                }
            ]
        };

        var goal = new ForecastEngine().Run(request).GoalOutcomes.Single();

        goal.IsAffordable.Should().BeTrue();
        goal.AffordableByDate.Should().Be(asOf);
    }

    [Theory]
    [InlineData(2025, 1, 31, 2025, 3, 1)]
    [InlineData(2025, 1, 1, 2025, 7, 1)]
    [InlineData(2025, 1, 15, 2025, 1, 20)]
    public void Engine_and_GoalMath_agree_on_required_monthly(int fy, int fm, int fd, int ty, int tm, int td)
    {
        var account = AccountId.New();
        var asOf = new DateOnly(fy, fm, fd);
        var target = new DateOnly(ty, tm, td);
        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, target),
            StartingBalances = new Dictionary<AccountId, Money> { [account] = new Money(0m, Usd) },
            AccountIds = [account],
            RecurringFlows = [],
            Goals =
            [
                new GoalAffordabilityInput
                {
                    GoalId = Guid.NewGuid(), Name = "G", TargetAmount = new Money(6000m, Usd),
                    CurrentBalance = new Money(0m, Usd), TargetDate = target, LinkedAccountId = account
                }
            ]
        };

        var engine = new ForecastEngine().Run(request).GoalOutcomes.Single().RequiredMonthlyContribution;

        engine.Should().Be(GoalMath.MonthlyNeeded(6000m, target, asOf));
    }

    [Fact]
    public void FirePlanner_sixty_year_horizon_yields_a_720_month_path()
    {
        var result = FirePlanner.Plan(1000m, 100m, 40000m, 4m, 5m, 30m, horizonYears: 60);

        result.Path.Count.Should().Be(721); // month 0 .. 720
        result.Path[^1].Month.Should().Be(720);
    }

    [Fact]
    public void Apy_compounded_monthly_over_a_year_equals_the_quoted_apy()
    {
        var path = Simulators.Grow(1000m, 0m, 4.5m, 12);

        path[^1].Balance.Should().Be(1045.00m);
        GoalPlanner.Path(1000m, 0m, 4.5m, 12)[^1].Should().BeApproximately(1045m, 0.001m);
        GoalPlanner.MonthsToReach(1045m, 1000m, 0m, 4.5m).Should().BeLessThanOrEqualTo(12);
    }

    [Fact]
    public void Shared_money_rounding_rounds_midpoints_away_from_zero()
    {
        MoneyRounding.Round(0.125m).Should().Be(0.13m);
        MoneyRounding.Round(-0.125m).Should().Be(-0.13m);
        MoneyRounding.Round(2.5m, 0).Should().Be(3m);
    }
}
