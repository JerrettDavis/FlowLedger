using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

/// <summary>
/// Unit tests for <see cref="RecurrenceExpander"/> — all recurrence pattern variants,
/// edge cases, leap years, and month-end clamping.
/// </summary>
public sealed class RecurrenceExpanderTests
{
    private static DateOnlyRange Horizon(DateOnly start, DateOnly end) =>
        new(start, end);

    // ── Daily ────────────────────────────────────────────────────────────────

    [Fact]
    public void Daily_produces_every_day_inclusive()
    {
        var pattern = RecurrencePattern.Daily();
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 5));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 1, 2),
            new DateOnly(2025, 1, 3),
            new DateOnly(2025, 1, 4),
            new DateOnly(2025, 1, 5),
        });
    }

    [Fact]
    public void Daily_respects_flow_start_later_than_horizon_start()
    {
        var pattern = RecurrencePattern.Daily();
        var flow = new DateOnly(2025, 1, 3);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 5));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 3),
            new DateOnly(2025, 1, 4),
            new DateOnly(2025, 1, 5),
        });
    }

    [Fact]
    public void Daily_respects_flow_end()
    {
        var pattern = RecurrencePattern.Daily();
        var flowStart = new DateOnly(2025, 1, 1);
        var flowEnd = new DateOnly(2025, 1, 3);  // exclusive per DateOnlyRange convention
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 5));

        var dates = RecurrenceExpander.Expand(pattern, flowStart, flowEnd, horizon);

        // flowEnd is exclusive, so Jan 3 is not included
        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 1, 2),
        });
    }

    // ── Weekly ────────────────────────────────────────────────────────────────

    [Fact]
    public void Weekly_on_friday_produces_all_fridays_in_range()
    {
        // Jan 2025: Fridays are 3, 10, 17, 24, 31
        var pattern = RecurrencePattern.Weekly(DayOfWeek.Friday);
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 3),
            new DateOnly(2025, 1, 10),
            new DateOnly(2025, 1, 17),
            new DateOnly(2025, 1, 24),
            new DateOnly(2025, 1, 31),
        });
    }

    [Fact]
    public void Weekly_anchor_is_first_occurrence_of_day_on_or_after_flow_start()
    {
        // flowStart is Wednesday Jan 8; Weekly on Friday → first Friday on or after Jan 8 = Jan 10
        var pattern = RecurrencePattern.Weekly(DayOfWeek.Friday);
        var flow = new DateOnly(2025, 1, 8);  // Wednesday
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().OnlyContain(d => d.DayOfWeek == DayOfWeek.Friday);
        dates.First().Should().Be(new DateOnly(2025, 1, 10));
    }

    // ── EveryNWeeks ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryTwoWeeks_biweekly_payroll_pattern()
    {
        // Biweekly payroll on Fridays, starting Jan 3 2025
        var pattern = RecurrencePattern.EveryNWeeks(2, DayOfWeek.Friday);
        var flow = new DateOnly(2025, 1, 3); // first Friday
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 2, 28));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        // Jan 3, Jan 17, Jan 31, Feb 14, Feb 28
        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 3),
            new DateOnly(2025, 1, 17),
            new DateOnly(2025, 1, 31),
            new DateOnly(2025, 2, 14),
            new DateOnly(2025, 2, 28),
        });
    }

    [Fact]
    public void EveryNWeeks_anchor_starts_from_first_matching_day_on_or_after_flow_start()
    {
        // flow starts Jan 6 (Monday), but anchor is Friday → first Friday on or after Jan 6 = Jan 10
        // Then every 2 weeks: Jan 10, Jan 24, Feb 7
        var pattern = RecurrencePattern.EveryNWeeks(2, DayOfWeek.Friday);
        var flow = new DateOnly(2025, 1, 6);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 2, 7));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 10),
            new DateOnly(2025, 1, 24),
            new DateOnly(2025, 2, 7),
        });
    }

    // ── TwiceMonthly ─────────────────────────────────────────────────────────

    [Fact]
    public void TwiceMonthly_produces_both_days_each_month()
    {
        var pattern = RecurrencePattern.TwiceMonthly(1, 15);
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 2, 28));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 1, 15),
            new DateOnly(2025, 2, 1),
            new DateOnly(2025, 2, 15),
        });
    }

    [Fact]
    public void TwiceMonthly_clamps_day31_in_february_non_leap()
    {
        // day1=31 in Feb → clamped to 28 (non-leap)
        var pattern = RecurrencePattern.TwiceMonthly(28, 31);
        var flow = new DateOnly(2025, 2, 1);
        var horizon = Horizon(new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 28));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        // Both day 28 and "day 31" clamp to Feb 28, so only one unique date
        dates.Should().ContainSingle().Which.Should().Be(new DateOnly(2025, 2, 28));
    }

    [Fact]
    public void TwiceMonthly_clamps_day29_in_february_leap_year()
    {
        // 2024 is a leap year — Feb has 29 days
        var pattern = RecurrencePattern.TwiceMonthly(15, 29);
        var flow = new DateOnly(2024, 2, 1);
        var horizon = Horizon(new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2024, 2, 15),
            new DateOnly(2024, 2, 29),
        });
    }

    // ── Monthly ───────────────────────────────────────────────────────────────

    [Fact]
    public void Monthly_produces_one_date_per_month()
    {
        var pattern = RecurrencePattern.Monthly(15);
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 6, 30));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().HaveCount(6);
        dates.Should().OnlyContain(d => d.Day == 15);
    }

    [Fact]
    public void Monthly_day31_clamps_in_short_months()
    {
        // Day 31: Jan 31, Feb 28, Mar 31, Apr 30, May 31
        var pattern = RecurrencePattern.Monthly(31);
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 5, 31));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 31),
            new DateOnly(2025, 2, 28),
            new DateOnly(2025, 3, 31),
            new DateOnly(2025, 4, 30),
            new DateOnly(2025, 5, 31),
        });
    }

    [Fact]
    public void Monthly_day29_in_feb_non_leap_clamps_to_28()
    {
        var pattern = RecurrencePattern.Monthly(29);
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 28));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().ContainSingle().Which.Should().Be(new DateOnly(2025, 2, 28));
    }

    [Fact]
    public void Monthly_day29_in_feb_leap_year_does_not_clamp()
    {
        var pattern = RecurrencePattern.Monthly(29);
        var flow = new DateOnly(2024, 1, 1);
        var horizon = Horizon(new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().ContainSingle().Which.Should().Be(new DateOnly(2024, 2, 29));
    }

    // ── LastBusinessDay ───────────────────────────────────────────────────────

    [Fact]
    public void LastBusinessDay_returns_correct_day_for_each_month()
    {
        // Jan 2025 last day = Friday 31
        // Feb 2025 last day = Friday 28
        // Mar 2025 last day = Monday 31
        // Apr 2025 last day = Wednesday 30
        var pattern = RecurrencePattern.LastBusinessDay();
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 4, 30));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEquivalentTo(new[]
        {
            new DateOnly(2025, 1, 31), // Friday
            new DateOnly(2025, 2, 28), // Friday
            new DateOnly(2025, 3, 31), // Monday
            new DateOnly(2025, 4, 30), // Wednesday
        });
        dates.Should().OnlyContain(d =>
            d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday);
    }

    [Fact]
    public void LastBusinessDay_saturday_rolls_back_to_friday()
    {
        // Nov 2025: last day Nov 30 = Sunday → should be Friday Nov 28
        var nov30 = new DateOnly(2025, 11, 30);
        nov30.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        var lbd = RecurrenceExpander.LastBusinessDayOfMonth(2025, 11);
        lbd.Should().Be(new DateOnly(2025, 11, 28));
        lbd.DayOfWeek.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public void LastBusinessDay_sunday_rolls_back_to_friday()
    {
        // Aug 2025: last day Aug 31 = Sunday → Friday Aug 29
        var aug31 = new DateOnly(2025, 8, 31);
        aug31.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        var lbd = RecurrenceExpander.LastBusinessDayOfMonth(2025, 8);
        lbd.Should().Be(new DateOnly(2025, 8, 29));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_returns_empty_when_flow_start_after_horizon_end()
    {
        var pattern = RecurrencePattern.Monthly(1);
        var flow = new DateOnly(2026, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        dates.Should().BeEmpty();
    }

    [Fact]
    public void Expand_requires_finite_horizon_end()
    {
        var pattern = RecurrencePattern.Monthly(1);
        var horizon = new DateOnlyRange(new DateOnly(2025, 1, 1));  // open-ended

        var act = () => RecurrenceExpander.Expand(pattern, new DateOnly(2025, 1, 1), null, horizon);
        act.Should().Throw<ArgumentException>().WithMessage("*finite horizon end*");
    }

    [Fact]
    public void NextOrSameDayOfWeek_when_from_is_already_correct_day()
    {
        var friday = new DateOnly(2025, 1, 3); // this is a Friday
        friday.DayOfWeek.Should().Be(DayOfWeek.Friday);

        var result = RecurrenceExpander.NextOrSameDayOfWeek(friday, DayOfWeek.Friday);
        result.Should().Be(friday);
    }

    [Fact]
    public void NextOrSameDayOfWeek_advances_to_next_week_if_day_passed()
    {
        // Jan 4 2025 is Saturday; next Sunday = Jan 5
        var saturday = new DateOnly(2025, 1, 4);
        var next = RecurrenceExpander.NextOrSameDayOfWeek(saturday, DayOfWeek.Sunday);
        next.Should().Be(new DateOnly(2025, 1, 5));
    }

    [Fact]
    public void Monthly_does_not_include_day_before_horizon_start()
    {
        // Flow starts Jan 1, monthly on day 5, horizon starts Jan 6
        var pattern = RecurrencePattern.Monthly(5);
        var flow = new DateOnly(2025, 1, 1);
        var horizon = Horizon(new DateOnly(2025, 1, 6), new DateOnly(2025, 3, 31));

        var dates = RecurrenceExpander.Expand(pattern, flow, null, horizon);

        // Jan 5 is before horizon start, so first date should be Feb 5
        dates.First().Should().Be(new DateOnly(2025, 2, 5));
    }
}
