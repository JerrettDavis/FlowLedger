using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

public sealed class RecurrencePatternTests
{
    [Fact]
    public void Monthly_creates_pattern_with_day_of_month()
    {
        var pattern = RecurrencePattern.Monthly(15);
        pattern.Frequency.Should().Be(RecurrenceFrequency.Monthly);
        pattern.DayOfMonth.Should().Be(15);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void Monthly_invalid_day_throws(int day)
    {
        var act = () => RecurrencePattern.Monthly(day);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TwiceMonthly_requires_first_less_than_second()
    {
        var act = () => RecurrencePattern.TwiceMonthly(15, 1);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TwiceMonthly_valid_creates_correctly()
    {
        var p = RecurrencePattern.TwiceMonthly(1, 15);
        p.DayOfMonth.Should().Be(1);
        p.SecondDayOfMonth.Should().Be(15);
    }

    [Fact]
    public void EveryNWeeks_requires_at_least_2_weeks()
    {
        var act = () => RecurrencePattern.EveryNWeeks(1, DayOfWeek.Friday);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Weekly_stores_anchor_day()
    {
        var p = RecurrencePattern.Weekly(DayOfWeek.Friday);
        p.AnchorDayOfWeek.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public void LastBusinessDay_creates_correctly()
    {
        var p = RecurrencePattern.LastBusinessDay();
        p.Frequency.Should().Be(RecurrenceFrequency.LastBusinessDay);
    }
}
