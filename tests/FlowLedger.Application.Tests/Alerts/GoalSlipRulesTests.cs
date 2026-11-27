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

    private static TransactionDto Tx(bool credit, decimal amount, int daysAgo)
    {
        var b = TransactionDtoBuilder.A().OnAccount(Checking).WithAmount(amount).WithStatus("Posted").On(Today.AddDays(-daysAgo));
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
