using FlowLedger.Application.Features.BillSheet;
using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.BillSheet;

/// <summary>Bills that happen several times a month (a $55 mow every other week) must appear once per date.</summary>
public sealed class RepeatingBillTests
{
    private static readonly DateOnly Nov = new(2026, 11, 1);
    private static readonly DateOnly Dec = new(2026, 12, 1);

    // 2026-11-03 is a Tuesday.
    private static SheetBillData Mowing(string frequency = BillFrequency.EveryNWeeks, int? weeks = 2, DateOnly? anchor = null)
        => new(Guid.NewGuid(), "Bills", 0, "Mustang Mowing", null, false, true, false, false, false, null, 55m, null, null, null,
            "mustang mowing", true, frequency, weeks, anchor ?? new DateOnly(2026, 11, 3));

    [Fact]
    public void Fortnightly_bill_has_a_slot_for_every_date_in_the_month()
    {
        var slots = BillSchedule.Slots(Mowing(), Nov);
        slots.Should().Equal(new DateOnly?[] { new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 17) });

        // Three Tuesdays line up in a month that starts early in the cycle.
        BillSchedule.Slots(Mowing(), Dec).Should().Equal(new DateOnly?[] { new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), new DateOnly(2026, 12, 29) });
    }

    [Fact]
    public void Cadence_continues_backwards_before_the_anchor()
    {
        BillSchedule.Slots(Mowing(), new DateOnly(2026, 10, 1))
            .Should().Equal(new DateOnly?[] { new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 20) });
    }

    [Fact]
    public void Weekly_bill_appears_every_week_and_twice_monthly_twice()
    {
        BillSchedule.Slots(Mowing(BillFrequency.Weekly, null), Nov).Should().HaveCount(4);

        var rent = Mowing(BillFrequency.TwiceMonthly, null) with { DueDay = 1, SecondDueDay = 15, AnchorDate = null };
        BillSchedule.Slots(rent, Nov).Should().Equal(new DateOnly?[] { new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15) });
    }

    [Fact]
    public void Monthly_bill_keeps_a_single_slot()
    {
        var b = Mowing(BillFrequency.Monthly, null) with { DueDay = 5 };
        BillSchedule.Slots(b, Nov).Should().Equal(new DateOnly?[] { new DateOnly(2026, 11, 5) });
        BillSchedule.Slots(b with { DueDay = null }, Nov).Should().Equal(new DateOnly?[] { null });
    }

    [Fact]
    public void Each_payment_ticks_its_own_slot()
    {
        var bill = Mowing();
        var txns = new[]
        {
            new SheetTxn(Guid.NewGuid(), new DateOnly(2026, 11, 3), 55m, "MUSTANG MOWING"),
            new SheetTxn(Guid.NewGuid(), new DateOnly(2026, 11, 17), 55m, "MUSTANG MOWING"),
        };

        var r = SheetMatcher.Match([bill], [], txns, [Nov]);

        r.Upserts.Should().HaveCount(2);
        r.Upserts.Select(p => p.Slot).Should().BeEquivalentTo(new[] { 0, 1 });
        r.Upserts.Single(p => p.Slot == 1).PaidOn.Should().Be(new DateOnly(2026, 11, 17));
    }

    [Fact]
    public void Auto_mark_for_a_slot_that_no_longer_exists_is_cleared()
    {
        var bill = Mowing();
        var t = new SheetTxn(Guid.NewGuid(), new DateOnly(2026, 11, 29), 55m, "MUSTANG MOWING");
        var stale = new SheetPaymentData(bill.Id, Nov, true, 55m, t.Date, t.Id, SheetPaymentSource.Auto, Slot: 2);

        SheetMatcher.Match([bill], [stale], [t], [Nov]).Clears.Should().Contain((bill.Id, Nov, 2));
    }

    [Fact]
    public void Monthly_equivalent_of_a_fortnightly_flow_counts_every_occurrence()
    {
        var p = RecurrencePattern.EveryNWeeks(2, DayOfWeek.Tuesday);
        (55m * p.OccurrencesPerMonth).Should().BeApproximately(119.17m, 0.01m); // ~$100+ a month, not a single $55
        RecurrencePattern.Weekly(DayOfWeek.Monday).OccurrencesPerMonth.Should().BeApproximately(4.33m, 0.01m);
        RecurrencePattern.TwiceMonthly(1, 15).OccurrencesPerMonth.Should().Be(2m);
        RecurrencePattern.Monthly(3).OccurrencesPerMonth.Should().Be(1m);
    }

    [Fact]
    public void Detector_finds_the_fortnightly_mow()
    {
        var acct = Guid.NewGuid();
        var dates = Enumerable.Range(0, 6).Select(i => new DateOnly(2026, 9, 1).AddDays(14 * i)).ToArray();
        var tx = dates.Select(d => new FlowTxn(acct, d, 55m, false, "MUSTANG MOWING")).ToList();

        var f = RecurringFlowDetector.Detect(tx, new DateOnly(2026, 11, 14)).Single();
        f.Frequency.Should().Be("EveryNWeeks");
        f.IntervalWeeks.Should().Be(2);
        f.Amount.Should().Be(55m);
    }
}
