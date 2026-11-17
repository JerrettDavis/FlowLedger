using FlowLedger.Application.Features.RecurringFlows;
using FluentAssertions;

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
    public void Splits_monthly_bonus_week_into_separate_extra_flow()
    {
        // Real shape: weekly ~$1,550 checks, with one larger check in the same part of each month.
        var weekly = new (DateOnly, decimal)[]
        {
            (D(2026, 8, 19), 1565.84m), (D(2026, 8, 26), 1562.73m), (D(2026, 9, 2), 1538.54m), (D(2026, 9, 10), 1550.28m),
            (D(2026, 9, 16), 3861.92m), (D(2026, 9, 23), 1552.05m), (D(2026, 9, 30), 1546.45m), (D(2026, 10, 7), 1539.45m),
            (D(2026, 10, 14), 3406.62m), (D(2026, 10, 21), 1534.84m), (D(2026, 10, 28), 1562.21m), (D(2026, 11, 4), 1549.50m),
            (D(2026, 11, 12), 3155.30m),
        };
        var tx = weekly.Select(w => new FlowTxn(Acct, w.Item1, w.Item2, true, "QTR CORPORATION PAYROLLQTC JERRETT D DAVIS")).ToList();

        var flows = RecurringFlowDetector.Detect(tx, D(2026, 11, 16));
        flows.Should().HaveCount(2);

        var basePay = flows.Single(f => !f.IsPeriodicExtra);
        basePay.Frequency.Should().Be("Weekly");
        basePay.Amount.Should().BeInRange(1540m, 1555m);
        basePay.AmountModel.Should().Be("Fixed");

        var extra = flows.Single(f => f.IsPeriodicExtra);
        extra.Frequency.Should().Be("Monthly");
        extra.Direction.Should().Be("Credit");
        extra.Name.Should().EndWith(RecurringFlowDetector.ExtraSuffix);
        extra.DayOfMonth.Should().Be(14);
        extra.Amount.Should().BeInRange(1850m, 1950m);
        extra.Key.Should().NotBe(basePay.Key);
        extra.Confidence.Should().BeGreaterThanOrEqualTo(0.9);
    }

    [Fact]
    public void One_off_large_deposit_does_not_create_an_extra()
    {
        var tx = new List<FlowTxn>();
        for (var i = 0; i < 8; i++)
        {
            tx.Add(new FlowTxn(Acct, D(2026, 9, 23).AddDays(7 * i), i == 3 ? 4000m : 1550m, true, "QTR PAYROLL"));
        }

        RecurringFlowDetector.Detect(tx, D(2026, 11, 16)).Should().ContainSingle().Which.IsPeriodicExtra.Should().BeFalse();
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

    [Fact]
    public void Merges_paypal_descriptor_variants_with_city_or_phone_suffix()
    {
        // Real Citi shape: the trailing "<city> <state>" vs "<phone> <state>" differs month to month.
        var tx = new List<FlowTxn>
        {
            new(Acct, D(2026, 8, 30), 6.50m, false, "PAYPAL *NABU CASA NABU 4029357733 CA"),
            new(Acct, D(2026, 9, 30), 6.50m, false, "PAYPAL *NABU CASA NABU 4029357733 CA"),
            new(Acct, D(2026, 10, 30), 6.50m, false, "PAYPAL *NABU CASA NABU ROHNERT PAR CA"),
            new(Acct, D(2026, 9, 1), 8.40m, false, "PP*GOOGLE LLC GOOGLE W 4029357733 CA"),
            new(Acct, D(2026, 10, 1), 8.40m, false, "PP*GOOGLE LLC GOOGLE W 4029357733 CA"),
            new(Acct, D(2026, 11, 1), 8.40m, false, "PP*GOOGLE LLC GOOGLE W MOUNTAIN VI CA"),
            // Other Google Pay purchases must not be folded into the G-Suite flow.
            new(Acct, D(2026, 10, 4), 9.99m, false, "PP*GOOGLE NIANTIC INC 4029357733 CA"),
            new(Acct, D(2026, 10, 11), 9.99m, false, "PP*GOOGLE NIANTIC INC 4029357733 CA"),
            new(Acct, D(2026, 11, 2), 9.99m, false, "PP*GOOGLE NIANTIC INC MOUNTAIN VI CA"),
        };

        var flows = RecurringFlowDetector.Detect(tx, Today);

        flows.Should().HaveCount(2);
        flows.Should().ContainSingle(f => f.Name.StartsWith("NABU CASA") && f.Amount == 6.50m && f.DayOfMonth == 28);
        flows.Should().ContainSingle(f => f.Name.StartsWith("GOOGLE LLC GOOGLE W") && f.Amount == 8.40m && f.DayOfMonth == 1);
    }

    [Fact]
    public void Tolerates_one_off_amount_outlier_on_a_monthly_subscription()
    {
        var tx = Series("GITHUB INC SAN FRANCIS CA", false, 43m, D(2026, 8, 11), D(2026, 9, 11), D(2026, 10, 12)).ToList();
        tx.Add(new FlowTxn(Acct, D(2026, 11, 11), 4m, false, "GITHUB INC SAN FRANCIS CA"));

        RecurringFlowDetector.Detect(tx, Today).Should().ContainSingle(f => f.Name.StartsWith("GITHUB"));
    }

    [Fact]
    public void Ignores_gas_station_even_when_it_looks_monthly()
    {
        var tx = Series("CASEYS #4071 OKLAHOMA CITY OK", false, 16.14m, D(2026, 8, 22), D(2026, 9, 22), D(2026, 10, 22)).ToList();
        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty();
    }

    [Fact]
    public void Ignores_variable_monthly_restaurant_visits()
    {
        var tx = new List<FlowTxn>
        {
            new(Acct, D(2026, 8, 20), 31.20m, false, "SOME BISTRO TULSA OK"),
            new(Acct, D(2026, 9, 20), 58.75m, false, "SOME BISTRO TULSA OK"),
            new(Acct, D(2026, 10, 21), 22.10m, false, "SOME BISTRO TULSA OK"),
        };
        RecurringFlowDetector.Detect(tx, Today).Should().BeEmpty();
    }

    [Fact]
    public void Keeps_variable_utility_bill()
    {
        var tx = new List<FlowTxn>
        {
            new(Acct, D(2026, 8, 4), 482m, false, "AMER ELECT PWR CPPWDRAWAL"),
            new(Acct, D(2026, 9, 4), 553m, false, "AMER ELECT PWR CPPWDRAWAL"),
            new(Acct, D(2026, 10, 4), 520m, false, "AMER ELECT PWR CPPWDRAWAL"),
        };
        RecurringFlowDetector.Detect(tx, Today).Should().ContainSingle();
    }

    [Fact]
    public void Dismissal_matches_legacy_and_normalized_keys()
    {
        var d = RecurringFlowDetector.Detect(
            Series("PP*GOOGLE LLC GOOGLE W 4029357733 CA", false, 8.40m, D(2026, 9, 1), D(2026, 10, 1), D(2026, 11, 1)).ToList(), Today).Single();
        var parts = d.Key.Split('|');

        RecurringSuggestionService.IsDismissed(d, new HashSet<string> { d.Key }).Should().BeTrue();
        RecurringSuggestionService.IsDismissed(d, new HashSet<string> { $"{parts[0]}|D|ppgooglellcgooglewmountainvica" }).Should().BeTrue();
        RecurringSuggestionService.IsDismissed(d, new HashSet<string> { $"{parts[0]}|C|googlellcgooglew" }).Should().BeFalse();
        RecurringSuggestionService.IsDismissed(d, new HashSet<string> { $"{parts[0]}|D|netflix" }).Should().BeFalse();
    }

    [Fact]
    public void Detects_monthly_bill_whose_day_straddles_the_month_end()
    {
        var tx = Series("PAYPAL *DIGITALOCEA BROOMFIELD CO", false, 6m, D(2026, 9, 1), D(2026, 9, 30), D(2026, 11, 1)).ToList();
        RecurringFlowDetector.Detect(tx, Today).Should().ContainSingle(f => f.Name.StartsWith("DIGITALOCEA"));
    }

    [Fact]
    public void Detects_subscription_with_one_cheaper_month()
    {
        var tx = new List<FlowTxn>
        {
            new(Acct, D(2026, 8, 25), 14.99m, false, "PP*HUMBLEBUNDL HUMBLEB 4029357733 CA"),
            new(Acct, D(2026, 9, 28), 14.99m, false, "PP*HUMBLEBUNDL HUMBLEB 4029357733 CA"),
            new(Acct, D(2026, 10, 25), 11.87m, false, "PP*HUMBLEBUNDL HUMBLEB SAN FRANCIS CA"),
        };
        RecurringFlowDetector.Detect(tx, Today).Should().ContainSingle(f => f.Name.StartsWith("HUMBLEBUNDL"));
    }
}
