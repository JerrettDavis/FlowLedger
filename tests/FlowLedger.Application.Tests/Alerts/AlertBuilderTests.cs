using FlowLedger.Application.Features.Alerts;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Alerts;

public sealed class AlertBuilderTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);

    [Fact]
    public void Over_budget_is_error_and_sorted_first()
    {
        var alerts = AlertBuilder.Build(
            [new("Food", 100m, 85m), new("Fun", 50m, 75m)], [], Today);

        alerts.Should().HaveCount(2);
        alerts[0].Severity.Should().Be("error");
        alerts[0].Title.Should().Contain("Fun");
        alerts[1].Severity.Should().Be("warning");
    }

    [Fact]
    public void Under_threshold_and_zero_limit_budgets_produce_nothing()
    {
        AlertBuilder.Build([new("A", 100m, 79m), new("B", 0m, 10m)], [], Today).Should().BeEmpty();
    }

    [Fact]
    public void Exactly_at_limit_is_warning_not_error()
    {
        var alerts = AlertBuilder.Build([new("A", 100m, 100m)], [], Today);

        alerts.Should().ContainSingle().Which.Severity.Should().Be("warning");
    }

    [Fact]
    public void Goal_past_target_date_and_unfinished_alerts_but_finished_or_future_does_not()
    {
        var alerts = AlertBuilder.Build([], [
            new("Late", 1000m, 200m, new DateOnly(2026, 10, 1)),
            new("Done", 1000m, 1000m, new DateOnly(2026, 10, 1)),
            new("Future", 1000m, 0m, new DateOnly(2027, 1, 1)),
            new("NoDate", 1000m, 0m, null)], Today);

        alerts.Should().ContainSingle().Which.Title.Should().Contain("Late");
    }
}
