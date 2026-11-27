using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>Pins the wire contract of /api/budgets, /api/goals and /api/alerts.</summary>
[Collection("ApiIntegration")]
public sealed class BudgetAlertEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Put_budget_with_negative_limit_returns_validation_problem_keyed_budget()
    {
        var response = await _client.PutAsJsonAsync("/api/budgets", new { category = "Food", monthlyLimit = -1m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("budget")[0].GetString()
            .Should().Be("A category and a non-negative limit are required.");
    }

    [Fact]
    public async Task Put_then_list_budgets_returns_month_budgets_and_spend()
    {
        (await _client.PutAsJsonAsync("/api/budgets", new { category = " Food ", monthlyLimit = 250m }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/budgets"));
        var root = doc.RootElement;
        root.GetProperty("month").GetString().Should().Be(DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        root.GetProperty("spendByCategory").ValueKind.Should().Be(JsonValueKind.Object);
        var budget = root.GetProperty("budgets").EnumerateArray().Single(b => b.GetProperty("category").GetString() == "Food");
        budget.GetProperty("monthlyLimit").GetDecimal().Should().Be(250m);
        budget.GetProperty("spent").GetDecimal().Should().Be(0m);
        budget.GetProperty("remaining").GetDecimal().Should().Be(250m);
        budget.GetProperty("percentUsed").GetDouble().Should().Be(0d);
        budget.GetProperty("over").GetBoolean().Should().BeFalse();

        (await _client.DeleteAsync("/api/budgets?category=Food")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Put_goal_with_zero_target_returns_validation_problem_keyed_goal()
    {
        var response = await _client.PutAsJsonAsync("/api/goals", new { name = "Car", targetAmount = 0m, savedAmount = 0m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors").GetProperty("goal")[0].GetString()
            .Should().Be("A name, a positive target and a non-negative saved amount are required.");
    }

    [Fact]
    public async Task Alerts_flag_overdue_goal()
    {
        (await _client.PutAsJsonAsync("/api/goals", new { name = "Late", targetAmount = 100m, savedAmount = 0m, targetDate = "2000-01-01" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/alerts"));
        doc.RootElement.EnumerateArray()
            .Should().Contain(a => a.GetProperty("title").GetString() == "Goal \"Late\" is past its target date"
                && a.GetProperty("severity").GetString() == "warning");
    }
}
