using FlowLedger.Application.Features.RecurringFlows;
using FluentAssertions;

namespace FlowLedger.Application.Tests.RecurringFlows;

/// <summary>Edge cases around cadence classification, amount tolerance, duplicate/unusable rows and account boundaries.</summary>
public sealed class RecurringFlowDetectorEdgeCaseTests
{
    private static readonly Guid Acct = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 14);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static List<FlowTxn> Series(string text, bool credit, decimal amount, params DateOnly[] dates) =>
        dates.Select(d => new FlowTxn(Acct, d, amount, credit, text)).ToList();

    private static List<FlowTxn> Amounts(string text, bool credit, DateOnly[] dates, params decimal[] amounts) =>
        dates.Select((d, i) => new FlowTxn(Acct, d, amounts[i], credit, text)).ToList();

    private static readonly DateOnly[] Monthly5th = [D(2026, 8, 5), D(2026, 9, 5), D(2026, 10, 5), D(2026, 11, 5)];

    // ── Cadence: biweekly vs semimonthly ─────────────────────────────────────

    [Fact]
    public void Every_14_days_is_biweekly_not_twice_monthly_even_when_a_month_gets_three_paydays()
    {
        // Fridays every 14 days: October 2 / 16 / 30 puts three paydays in one month, which twice-monthly must not accept.
        var start = D(2026, 8, 7);
        var tx = Series("ACME PAYROLL", true, 2000m, Enumerable.Range(0, 8).Select(i => start.AddDays(14 * i)).ToArray());

        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.Frequency.Should().Be("EveryNWeeks");
        f.IntervalWeeks.Should().Be(2);
        f.DayOfMonth.Should().BeNull();
        f.SecondDayOfMonth.Should().BeNull();
        f.AnchorDayOfWeek.Should().Be("Friday");
    }

    [Fact]
    public void The_1st_and_15th_is_twice_monthly_not_biweekly_despite_14_day_gaps_inside_each_month()
    {
        var tx = Series("ACME PAYROLL", true, 2000m,
            D(2026, 8, 1), D(2026, 8, 15), D(2026, 9, 1), D(2026, 9, 15), D(2026, 10, 1), D(2026, 10, 15), D(2026, 11, 1));

        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.Frequency.Should().Be("TwiceMonthly");
        f.DayOfMonth.Should().Be(1);
        f.SecondDayOfMonth.Should().Be(15);
        f.IntervalWeeks.Should().BeNull();
    }

    [Fact]
    public void Biweekly_tolerates_one_day_holiday_shifts()
    {
        var tx = Series("ACME PAYROLL", true, 2000m, D(2026, 9, 4), D(2026, 9, 18), D(2026, 10, 3), D(2026, 10, 16), D(2026, 10, 30));

        RecurringFlowDetector.Detect(tx, Today).Should().ContainSingle(f => f.Frequency == "EveryNWeeks");
    }

    [Fact]
    public void More_history_yields_higher_confidence_for_the_same_cadence()
    {
        var short3 = Series("ACME PAYROLL", true, 2000m, D(2026, 10, 2), D(2026, 10, 16), D(2026, 10, 30));
        var long6 = Series("ACME PAYROLL", true, 2000m, D(2026, 8, 21), D(2026, 9, 4), D(2026, 9, 18), D(2026, 10, 2), D(2026, 10, 16), D(2026, 10, 30));

        var s = RecurringFlowDetector.Detect(short3, Today).Single();
        var l = RecurringFlowDetector.Detect(long6, Today).Single();

        l.Confidence.Should().BeGreaterThan(s.Confidence);
    }

    [Fact]
    public void Three_weekly_deposits_are_not_enough_but_four_are()
    {
        var three = Series("GIG PAY", true, 400m, D(2026, 10, 23), D(2026, 10, 30), D(2026, 11, 6));
        var four = Series("GIG PAY", true, 400m, D(2026, 10, 16), D(2026, 10, 23), D(2026, 10, 30), D(2026, 11, 6));

        RecurringFlowDetector.Detect(three, Today).Should().BeEmpty();
        RecurringFlowDetector.Detect(four, Today).Should().ContainSingle(f => f.Frequency == "Weekly");
    }

    // ── Amount tolerance ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 103, 97, "Fixed")]       // within 3% of the median
    [InlineData(100, 104, 96, "Estimated")]   // just outside 3%
    public void Fixed_versus_estimated_follows_the_three_percent_band(double a, double b, double c, string model)
    {
        var tx = Amounts("STREAMCO", false, Monthly5th[..3], (decimal)a, (decimal)b, (decimal)c);

        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.AmountModel.Should().Be(model);
        f.Amount.Should().Be(100m, "the reported amount is the median of the observations");
    }

    [Fact]
    public void Wildly_varying_non_bill_expense_is_not_recurring_even_on_a_stable_day()
    {
        var tx = Amounts("RANDOM SHOP", false, Monthly5th[..3], 30m, 95m, 55m);

        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty();
    }

    [Fact]
    public void Income_within_35_percent_is_estimated_but_wider_swings_are_rejected()
    {
        var near = Amounts("CONSULTING LLC", true, Monthly5th[..3], 1000m, 1300m, 700m);
        var wide = Amounts("CONSULTING LLC", true, Monthly5th[..3], 1000m, 2000m, 500m);

        RecurringFlowDetector.Detect(near, Today).Single().AmountModel.Should().Be("Estimated");
        RecurringFlowDetector.Detect(wide, Today).Should().BeEmpty();
    }

    // ── Duplicates / unusable rows ───────────────────────────────────────────

    [Fact]
    public void A_duplicate_posting_on_the_same_day_is_one_occurrence()
    {
        var tx = Series("STREAMCO", false, 15m, Monthly5th[..3]);
        tx.Add(new FlowTxn(Acct, Monthly5th[2], 15m, false, "STREAMCO"));

        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.Occurrences.Should().Be(3);
        f.Amount.Should().Be(15m, "the median still reflects the usual charge");
    }

    [Fact]
    public void Zero_negative_and_blank_rows_are_ignored()
    {
        // Pending/placeholder-style rows that carry no usable amount or description never count as an occurrence.
        var tx = Series("STREAMCO", false, 15m, Monthly5th[0], Monthly5th[1]);
        tx.Add(new FlowTxn(Acct, Monthly5th[2], 0m, false, "STREAMCO"));
        tx.Add(new FlowTxn(Acct, Monthly5th[3], -15m, false, "STREAMCO"));
        tx.AddRange(Series("   ", false, 15m, Monthly5th[2], Monthly5th[3]));

        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty("only two real occurrences remain");
    }

    [Fact]
    public void Empty_input_yields_no_flows()
    {
        RecurringFlowDetector.Detect([], Today).Should().BeEmpty();
    }

    [Fact]
    public void Input_order_does_not_matter()
    {
        var ordered = Series("STREAMCO", false, 15m, Monthly5th[..3]);
        var shuffled = ordered.AsEnumerable().Reverse().ToList();

        var a = RecurringFlowDetector.Detect(ordered, Today).Single();
        var b = RecurringFlowDetector.Detect(shuffled, Today).Single();

        b.Should().Be(a);
    }

    // ── Account / direction boundaries ───────────────────────────────────────

    [Fact]
    public void Occurrences_on_different_accounts_are_never_pooled()
    {
        var other = Guid.NewGuid();
        var tx = Series("STREAMCO", false, 15m, Monthly5th[0], Monthly5th[1]);
        tx.AddRange(new[] { Monthly5th[2], Monthly5th[3] }.Select(d => new FlowTxn(other, d, 15m, false, "STREAMCO")));

        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty("two on each account is below the three-occurrence minimum, and pooling would invent a pattern");
    }

    [Fact]
    public void A_refund_credit_does_not_join_the_debit_pattern_or_change_its_direction()
    {
        var tx = Series("STREAMCO", false, 15m, Monthly5th[..3]);
        tx.Add(new FlowTxn(Acct, D(2026, 11, 6), 15m, true, "STREAMCO"));

        var flows = RecurringFlowDetector.Detect(tx, Today);

        flows.Should().ContainSingle().Which.Direction.Should().Be("Debit");
        flows[0].Occurrences.Should().Be(3);
    }

    [Fact]
    public void Flow_keys_embed_the_account_so_other_accounts_cannot_collide()
    {
        var other = Guid.NewGuid();
        var mine = RecurringFlowDetector.Detect(Series("STREAMCO", false, 15m, Monthly5th[..3]), Today).Single();
        var theirs = RecurringFlowDetector.Detect(
            Monthly5th[..3].Select(d => new FlowTxn(other, d, 15m, false, "STREAMCO")).ToList(), Today).Single();

        mine.Key.Should().StartWith(Acct.ToString("N"));
        theirs.Key.Should().StartWith(other.ToString("N"));
        mine.Key.Should().NotBe(theirs.Key);
    }

    // ── Staleness ────────────────────────────────────────────────────────────

    [Fact]
    public void A_monthly_pattern_is_kept_until_it_has_missed_two_periods_and_dropped_after()
    {
        var tx = Series("STREAMCO", false, 15m, D(2026, 6, 10), D(2026, 7, 10), D(2026, 8, 10));
        var last = D(2026, 8, 10);

        RecurringFlowDetector.Detect(tx, last.AddDays(65)).Should().ContainSingle();
        RecurringFlowDetector.Detect(tx, last.AddDays(66)).Should().BeEmpty();
    }

    [Fact]
    public void Next_expected_is_never_in_the_past()
    {
        var tx = Series("STREAMCO", false, 15m, D(2026, 8, 20), D(2026, 9, 20), D(2026, 10, 20));

        var f = RecurringFlowDetector.Detect(tx, Today).Single();

        f.NextExpected.Should().BeOnOrAfter(Today);
        f.NextExpected.Should().Be(D(2026, 11, 20));
    }
}
