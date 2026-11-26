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

    [Theory]
    [InlineData(200_000, 6, 360, 1199.10)]   // textbook 30-year mortgage
    [InlineData(10_000, 5, 12, 856.07)]
    [InlineData(1_200, 0, 12, 100.00)]       // zero rate amortises linearly
    public void LoanPayment_matches_the_amortisation_formula_to_the_cent(decimal balance, decimal rate, int months, decimal expected)
        => LoanMath.Payment(balance, rate, months).Should().Be(expected);

    [Fact]
    public void LoanPayment_survives_extreme_terms_and_rates_without_overflowing()
    {
        // (1 + r)^n would overflow decimal here; the discounted form shrinks toward zero instead.
        var payment = LoanMath.Payment(1_000_000m, 900m, 1_200);

        payment.Should().BeApproximately(750_000m, 1m, "interest-only at 75% a month");
    }

    [Theory]
    [InlineData(10_000, 0, 5, 12)]
    [InlineData(1_000_000, 0, 7, 720)]
    [InlineData(5_000, 2_000, 4, 24)]
    public void RequiredMonthly_deposits_reach_the_target(decimal target, decimal start, decimal apy, int months)
    {
        var deposit = GoalPlanner.RequiredMonthly(target, start, apy, months);

        var balance = start;
        var rate = Simulators.MonthlyRate(apy);
        for (var m = 0; m < months; m++)
        {
            balance += balance * rate + deposit;
        }

        // Within the rounding of the deposit to the cent over the whole term.
        balance.Should().BeInRange(target - 0.01m * months, target + 0.01m * months * 2);
    }

    [Fact]
    public void RequiredMonthly_is_zero_when_the_start_already_grows_to_the_target_and_never_overflows()
    {
        GoalPlanner.RequiredMonthly(1_000m, 5_000m, 7m, 12).Should().Be(0m);
        var act = () => GoalPlanner.RequiredMonthly(1_000_000m, 1m, 900m, 720);
        act.Should().NotThrow();
    }

    [Fact]
    public void RequiredMonthly_with_a_zero_rate_is_linear()
        => GoalPlanner.RequiredMonthly(1_200m, 0m, 0m, 12).Should().Be(100m);
}
