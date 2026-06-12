using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

public sealed class DateOnlyRangeTests
{
    [Fact]
    public void Contains_returns_true_for_date_within_range()
    {
        var range = new DateOnlyRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31));
        range.Contains(new DateOnly(2024, 6, 15)).Should().BeTrue();
    }

    [Fact]
    public void Contains_returns_false_for_date_on_end_boundary_half_open()
    {
        var range = new DateOnlyRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31));
        // Half-open: end is exclusive
        range.Contains(new DateOnly(2024, 12, 31)).Should().BeFalse();
    }

    [Fact]
    public void Contains_returns_true_for_start_date()
    {
        var start = new DateOnly(2024, 3, 1);
        var range = new DateOnlyRange(start, new DateOnly(2024, 4, 1));
        range.Contains(start).Should().BeTrue();
    }

    [Fact]
    public void Open_ended_range_contains_any_date_after_start()
    {
        var range = new DateOnlyRange(new DateOnly(2024, 1, 1));
        range.Contains(new DateOnly(2099, 1, 1)).Should().BeTrue();
        range.IsOpenEnded.Should().BeTrue();
    }

    [Fact]
    public void End_before_start_throws_ArgumentException()
    {
        var act = () => new DateOnlyRange(new DateOnly(2024, 6, 1), new DateOnly(2024, 1, 1));
        act.Should().Throw<ArgumentException>();
    }
}
