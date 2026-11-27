using FlowLedger.Application.Features.Alerts;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Alerts;

/// <summary>#70: an alert when a savings goal's projected completion slips past its target date.</summary>
public sealed class GoalSlipRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> NoTransfers = new HashSet<Guid>();

    private static TransactionDto Tx(bool credit, decimal amount, int daysAgo, Guid? account = null)
    {
        var b = TransactionDtoBuilder.A().OnAccount(account ?? Checking).WithAmount(amount).WithStatus("Posted").On(Today.AddDays(-daysAgo));
        return (credit ? b.Credit() : b.Debit()).Build();
    }

    /// <summary>Four months of history: 3000 in and 2000 out each month, so a surplus of 1000 a month.</summary>
    private static List<TransactionDto> FourMonthsWithSurplus1000() =>
        Enumerable.Range(0, 4).SelectMany(m => new[] { Tx(true, 3000m, 5 + m * 30), Tx(false, 2000m, 8 + m * 30) }).ToList();

    private static GoalPace Goal(string name, decimal target, decimal saved, DateOnly? date) => new(Guid.NewGuid(), name, target, saved, date);

    [Fact]
    public void The_surplus_is_the_monthly_average_of_money_in_minus_money_out_over_three_months()
    {
        var surplus = GoalSlipRules.MonthlySurplus(Today, FourMonthsWithSurplus1000(), NoTransfers);

        surplus.Should().Be(1000m);
    }

    [Fact]
    public void Transfers_do_not_count_towards_the_surplus()
    {
        var history = FourMonthsWithSurplus1000();
        var transfer = Tx(true, 5000m, 10);
        history.Add(transfer);

        GoalSlipRules.MonthlySurplus(Today, history, new HashSet<Guid> { transfer.Id }).Should().Be(1000m);
    }

    [Fact]
    public void Too_little_history_claims_no_pace()
    {
        GoalSlipRules.MonthlySurplus(Today, [Tx(true, 3000m, 5), Tx(false, 2000m, 8)], NoTransfers).Should().BeNull();
        GoalSlipRules.Build(Today, [Goal("Car", 10_000m, 0m, Today.AddMonths(2))], null).Should().BeEmpty();
    }

    [Fact]
    public void A_goal_the_pace_will_not_finish_in_time_raises_one_alert_with_a_stable_key()
    {
        var goal = Goal("Car", 12_000m, 0m, Today.AddMonths(6)); // needs 12 months at 1000 a month

        var alert = GoalSlipRules.Build(Today, [goal], 1000m).Should().ContainSingle().Subject;

        alert.Kind.Should().Be(ActivityAlertKinds.GoalSlipping);
        alert.Subject.Should().Be("Car");
        alert.Amount.Should().Be(12_000m);
        alert.DedupeKey.Should().Be($"goal_slipping:{goal.Id}:{goal.TargetDate:yyyy-MM-dd}");
        GoalSlipRules.Build(Today, [goal], 1000m)[0].DedupeKey.Should().Be(alert.DedupeKey, "the same condition keeps the same key so it is sent once");
    }

    [Fact]
    public void A_goal_the_pace_will_finish_in_time_raises_nothing()
    {
        GoalSlipRules.Build(Today, [Goal("Trip", 3_000m, 500m, Today.AddMonths(6))], 1000m).Should().BeEmpty();
    }

    [Fact]
    public void The_surplus_is_shared_between_unfinished_goals()
    {
        var a = Goal("A", 5_000m, 0m, Today.AddMonths(6)); // alone: 5 months, fine
        var b = Goal("B", 5_000m, 0m, Today.AddMonths(6)); // with a second goal each gets 500 a month: 10 months, late

        GoalSlipRules.Build(Today, [a], 1000m).Should().BeEmpty();
        GoalSlipRules.Build(Today, [a, b], 1000m).Select(x => x.Subject).Should().BeEquivalentTo(["A", "B"]);
    }

    // --- #70: a goal linked to an account uses THAT account's balance and 90-day trend instead of the household pace ---

    private static GoalPace Linked(string name, decimal target, decimal balance, decimal? trend, DateOnly? date)
    {
        var account = Guid.NewGuid();
        return new GoalPace(Guid.NewGuid(), name, target, Math.Max(0m, balance), date, new LinkedAccountPace(account, "Savings", balance, trend));
    }

    [Fact]
    public void A_linked_goal_is_projected_on_its_accounts_own_trend_not_the_household_surplus()
    {
        var onTrack = Linked("Fund", 12_000m, 6_000m, 1_000m, Today.AddMonths(8));  // 6 months at 1000: fine, whatever the household does
        var slipping = Linked("Boat", 12_000m, 6_000m, 250m, Today.AddMonths(8));  // 24 months at 250: late

        var alerts = GoalSlipRules.Build(Today, [onTrack, slipping], 100_000m);

        alerts.Should().ContainSingle().Which.Subject.Should().Be("Boat");
        alerts[0].Detail.Should().Contain("Savings's 90-day trend").And.Contain("6,000");
        alerts[0].Amount.Should().Be(6_000m, "the amount is what the account still has to gain");
    }

    [Fact]
    public void A_linked_goal_does_not_use_up_the_household_surplus_of_the_other_goals()
    {
        var linked = Linked("Fund", 50_000m, 0m, 5_000m, Today.AddMonths(24));
        var plain = Goal("Trip", 5_000m, 0m, Today.AddMonths(6)); // alone it gets the full 1000: 5 months, fine

        GoalSlipRules.Build(Today, [linked, plain], 1000m).Should().BeEmpty();
    }

    [Fact]
    public void A_linked_goal_with_a_falling_account_slips_and_a_funded_one_does_not()
    {
        GoalSlipRules.Build(Today, [Linked("Fund", 1_000m, 100m, -50m, Today.AddMonths(3))], null).Should().ContainSingle()
            .Which.Detail.Should().Contain("will not finish");
        GoalSlipRules.Build(Today, [Linked("Done", 1_000m, 1_200m, -50m, Today.AddMonths(3))], null).Should().BeEmpty();
    }

    [Fact]
    public void A_linked_goal_with_no_trend_falls_back_to_the_household_pace_on_the_account_balance()
    {
        var goal = Linked("Fund", 12_000m, 3_000m, null, Today.AddMonths(6)); // 9000 left at 1000/month: 9 months, late

        var alert = GoalSlipRules.Build(Today, [goal], 1000m).Should().ContainSingle().Subject;

        alert.Detail.Should().Contain("current pace").And.NotContain("90-day");
        alert.Amount.Should().Be(9_000m);
        GoalSlipRules.Build(Today, [goal], null).Should().BeEmpty("no pace at all can be claimed");
    }

    [Fact]
    public void Resolve_takes_progress_from_the_account_balance_and_the_pace_from_its_trend()
    {
        var account = Guid.NewGuid();
        var linkedGoal = new FlowLedger.Application.Features.Goals.GoalRow(Guid.NewGuid(), "Fund", 10_000m, 100m, Today.AddMonths(12), 0, account);
        var plainGoal = new FlowLedger.Application.Features.Goals.GoalRow(Guid.NewGuid(), "Trip", 2_000m, 400m, null);
        var missing = new FlowLedger.Application.Features.Goals.GoalRow(Guid.NewGuid(), "Orphan", 2_000m, 700m, null, 0, Guid.NewGuid());
        BalanceInput[] balances = [new(account, "Savings", 2_500m, true)];
        List<TransactionDto> history = [.. Enumerable.Range(0, 4).Select(m => Tx(true, 600m, 5 + m * 30, account))];

        var paces = GoalPaces.Resolve(Today, [linkedGoal, plainGoal, missing], balances, history);

        paces[0].Saved.Should().Be(2_500m, "the linked account's balance replaces the hand-entered 100");
        paces[0].Linked!.MonthlyTrend.Should().Be(600m, "three 600 credits fall inside the last 90 days, over three months");
        paces[1].Saved.Should().Be(400m);
        paces[1].Linked.Should().BeNull();
        paces[2].Saved.Should().Be(700m, "a link to an account that is gone keeps the hand-entered amount");
        paces[2].Linked.Should().BeNull();
    }

    [Fact]
    public void Unfinished_goals_are_grouped_by_their_account_for_the_low_balance_alert()
    {
        var shared = Guid.NewGuid();
        IReadOnlyList<GoalPace> paces =
        [
            new(Guid.NewGuid(), "B", 1000m, 10m, null, new LinkedAccountPace(shared, "Savings", 10m, null)),
            new(Guid.NewGuid(), "A", 1000m, 10m, null, new LinkedAccountPace(shared, "Savings", 10m, null)),
            new(Guid.NewGuid(), "Done", 5m, 10m, null, new LinkedAccountPace(shared, "Savings", 10m, null)),
            Goal("Plain", 1000m, 0m, null),
        ];

        GoalPaces.UnfinishedByAccount(paces).Should().ContainSingle().Which.Value.Should().Equal("A", "B");
    }

    [Fact]
    public void No_surplus_means_a_dated_goal_never_finishes_and_slips()
    {
        GoalSlipRules.Build(Today, [Goal("House", 1_000m, 0m, Today.AddMonths(12))], -50m).Should().ContainSingle();
    }

    [Fact]
    public void Funded_undated_and_already_late_goals_are_left_alone()
    {
        IReadOnlyList<GoalPace> goals =
        [
            Goal("Done", 1_000m, 1_000m, Today.AddMonths(1)),
            Goal("Someday", 1_000m, 0m, null),
            Goal("Late", 1_000m, 0m, Today.AddDays(-1)), // the "past its target date" alert owns this one
        ];

        GoalSlipRules.Build(Today, goals, 0m).Should().BeEmpty();
    }
}
