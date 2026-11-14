using FluentAssertions;
using FlowLedger.Application.Features.RecurringFlows;

namespace FlowLedger.Application.Tests.RecurringFlows;

public sealed class RecurringFlowDetectorTests
{
    private static readonly Guid Acct = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 14);

    private static IEnumerable<FlowTxn> Series(string text, bool credit, decimal amount, params DateOnly[] dates) =>
        dates.Select(d => new FlowTxn(Acct, d, amount, credit, text));

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Detects_fixed_monthly_bill_with_day_and_next_date()
    {
        var tx = Series("ROCKET MORTGAGE LOAN JERRETT 4411", false, 1267.87m, D(2026, 9, 3), D(2026, 10, 3), D(2026, 11, 3)).ToList();
        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.Frequency.Should().Be("Monthly");
        f.DayOfMonth.Should().Be(3);
        f.AmountModel.Should().Be("Fixed");
        f.Direction.Should().Be("Debit");
        f.Amount.Should().Be(1267.87m);
        f.NextExpected.Should().Be(D(2026, 12, 3));
        f.Name.Should().StartWith("ROCKET MORTGAGE");
        f.Confidence.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void Detects_biweekly_paycheck_with_anchor_weekday()
    {
        var tx = Series("QTR CORPORATION PAYROLL", true, 2100m, D(2026, 10, 2), D(2026, 10, 16), D(2026, 10, 30), D(2026, 11, 13)).ToList();
        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.Frequency.Should().Be("EveryNWeeks");
        f.IntervalWeeks.Should().Be(2);
        f.AnchorDayOfWeek.Should().Be("Friday");
        f.Direction.Should().Be("Credit");
        f.NextExpected.Should().Be(D(2026, 11, 27));
    }

    [Fact]
    public void Detects_weekly_variable_income_as_estimated()
    {
        var tx = new List<FlowTxn>();
        var amounts = new[] { 1200m, 1534m, 1900m, 1562m, 2100m };
        for (var i = 0; i < 5; i++)
        {
            tx.Add(new FlowTxn(Acct, D(2026, 10, 14).AddDays(7 * i), amounts[i], true, "PAYROLL"));
        }

        var f = RecurringFlowDetector.Detect(tx, Today).Single();
        f.Frequency.Should().Be("Weekly");
        f.AmountModel.Should().Be("Estimated");
    }

    [Fact]
    public void Split_deposit_on_one_day_counts_as_a_single_occurrence()
    {
        var tx = Series("PAYROLL", true, 1000m, D(2026, 9, 5), D(2026, 10, 5), D(2026, 11, 5)).ToList();
        tx.Add(new FlowTxn(Acct, D(2026, 11, 5), 500m, true, "PAYROLL"));
        RecurringFlowDetector.Detect(tx, Today).Single().Occurrences.Should().Be(3);
    }

    [Fact]
    public void Detects_twice_monthly_pay_on_two_stable_days()
    {
        var tx = Series("ACME PAY", true, 1800m,
            D(2026, 8, 15), D(2026, 8, 31), D(2026, 9, 15), D(2026, 9, 30), D(2026, 10, 15), D(2026, 10, 31), D(2026, 11, 14)).ToList();
        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.Frequency.Should().Be("TwiceMonthly");
        f.DayOfMonth.Should().Be(15);
        f.SecondDayOfMonth.Should().BeInRange(30, 31);
    }

    [Fact]
    public void Variable_monthly_utility_on_a_stable_day_is_estimated()
    {
        var tx = new[]
        {
            new FlowTxn(Acct, D(2026, 9, 12), 118m, false, "AMER ELECT PWR"),
            new FlowTxn(Acct, D(2026, 10, 12), 164m, false, "AMER ELECT PWR"),
            new FlowTxn(Acct, D(2026, 11, 12), 141m, false, "AMER ELECT PWR"),
        };
        var f = RecurringFlowDetector.Detect(tx, Today).Single();
        f.Frequency.Should().Be("Monthly");
        f.AmountModel.Should().Be("Estimated");
        f.Amount.Should().Be(141m);
    }

    [Fact]
    public void Daily_fuel_purchases_are_not_recurring()
    {
        var dates = Enumerable.Range(0, 20).Select(i => D(2026, 10, 20).AddDays(i)).ToArray();
        RecurringFlowDetector.Detect(Series("QUIKTRIP", false, 4.94m, dates).ToList(), Today).Should().BeEmpty();
    }

    [Fact]
    public void Weekly_variable_spending_is_a_habit_not_a_bill()
    {
        var amounts = new[] { 14m, 19m, 16m, 21m, 13m, 18m };
        var tx = amounts.Select((a, i) => new FlowTxn(Acct, D(2026, 10, 3).AddDays(7 * i), a, false, "TORCHYS")).ToList();
        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty();
    }

    [Fact]
    public void Irregular_shopping_is_not_recurring()
    {
        var tx = Series("TARGET", false, 52m, D(2026, 9, 3), D(2026, 9, 20), D(2026, 10, 14), D(2026, 11, 9)).ToList();
        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty();
    }

    [Fact]
    public void Cancelled_subscription_is_dropped_once_it_stops()
    {
        var tx = Series("OLD STREAMING", false, 15m, D(2026, 3, 8), D(2026, 4, 8), D(2026, 5, 8), D(2026, 6, 8)).ToList();
        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty();
    }

    [Fact]
    public void Two_occurrences_are_not_enough()
    {
        var tx = Series("GYM", false, 40m, D(2026, 10, 1), D(2026, 11, 1)).ToList();
        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty();
    }

    [Fact]
    public void Same_merchant_on_different_accounts_is_detected_separately()
    {
        var other = Guid.NewGuid();
        var tx = Series("NETFLIX", false, 15.49m, D(2026, 9, 9), D(2026, 10, 9), D(2026, 11, 9)).ToList();
        tx.AddRange(new[] { D(2026, 9, 9), D(2026, 10, 9), D(2026, 11, 9) }.Select(d => new FlowTxn(other, d, 15.49m, false, "NETFLIX")));
        var found = RecurringFlowDetector.Detect(tx, Today);
        found.Should().HaveCount(2);
        found.Select(f => f.Key).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Keys_are_stable_across_reference_numbers()
    {
        var a = RecurringFlowDetector.Detect(Series("ATT PAYMENT 1111", false, 90m, D(2026, 9, 5), D(2026, 10, 5), D(2026, 11, 5)).ToList(), Today).Single();
        var b = RecurringFlowDetector.Detect(Series("ATT PAYMENT 2222", false, 90m, D(2026, 9, 5), D(2026, 10, 5), D(2026, 11, 5)).ToList(), Today).Single();
        a.Key.Should().Be(b.Key);
    }
}
