using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Investments;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Investments;

public sealed class InvestmentPerformanceTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly Guid Acct = Guid.NewGuid();

    private static BalancePoint P(DateOnly d, decimal b) => new(d, Acct, b);

    [Fact]
    public void Deposits_are_not_counted_as_gain()
    {
        var history = new[] { P(Today.AddDays(-30), 10_000m) };
        var flows = new[] { new CashFlowPoint(Today.AddDays(-10), 1_000m) };

        var p = InvestmentPerformanceCalculator.Compute(history, flows, 11_500m, Today).First();

        p.NetContributions.Should().Be(1_000m);
        p.Gain.Should().Be(500m);
        p.GainPercent.Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public void Withdrawals_do_not_look_like_losses()
    {
        var history = new[] { P(Today.AddDays(-30), 10_000m) };
        var flows = new[] { new CashFlowPoint(Today.AddDays(-5), -2_000m) };

        var p = InvestmentPerformanceCalculator.Compute(history, flows, 8_100m, Today).First();

        p.Gain.Should().Be(100m);
    }

    [Fact]
    public void Short_history_uses_earliest_snapshot_and_reports_actual_days()
    {
        var history = new[] { P(Today.AddDays(-20), 5_000m) };

        var result = InvestmentPerformanceCalculator.Compute(history, [], 5_250m, Today);

        result.Should().OnlyContain(x => x.Days == 20 && x.Gain == 250m);
    }

    [Fact]
    public void Contributions_before_the_anchor_snapshot_are_ignored()
    {
        var history = new[] { P(Today.AddDays(-30), 10_000m) };
        var flows = new[] { new CashFlowPoint(Today.AddDays(-40), 5_000m) };

        InvestmentPerformanceCalculator.Compute(history, flows, 10_000m, Today).First().Gain.Should().Be(0m);
    }

    [Fact]
    public void No_history_or_zero_start_balance_has_no_percent()
    {
        InvestmentPerformanceCalculator.Compute([], [], 100m, Today).Should().BeEmpty();

        var zero = InvestmentPerformanceCalculator.Compute([P(Today.AddDays(-30), 0m)], [], 100m, Today);
        zero.First().GainPercent.Should().BeNull();
    }

    [Fact]
    public void Picks_latest_snapshot_on_or_before_period_start()
    {
        var history = new[] { P(Today.AddDays(-100), 1_000m), P(Today.AddDays(-31), 2_000m), P(Today.AddDays(-29), 9_000m) };

        var p = InvestmentPerformanceCalculator.Compute(history, [], 2_500m, Today).Single(x => x.Period == "30 days");

        p.StartBalance.Should().Be(2_000m);
    }

    [Fact]
    public void Custom_range_measures_between_the_chosen_dates_and_removes_deposits()
    {
        var history = new[] { P(new DateOnly(2026, 9, 1), 1000m), P(new DateOnly(2026, 10, 1), 1600m), P(new DateOnly(2026, 11, 14), 2000m) };
        var deposits = new[] { new CashFlowPoint(new DateOnly(2026, 9, 15), 400m) };

        // Sept 1 -> Oct 1: 1000 -> 1600 with 400 deposited => 200 gain, ending balance taken from the Oct 1 snapshot.
        var r = InvestmentPerformanceCalculator.ComputeRange(history, deposits, 2000m, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), Today);

        r.Should().ContainSingle();
        r[0].Gain.Should().Be(200m);
        r[0].NetContributions.Should().Be(400m);
    }
}
