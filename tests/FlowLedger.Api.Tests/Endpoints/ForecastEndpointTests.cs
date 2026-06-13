using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class ForecastEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── 400 with no accounts (empty tenant) ──────────────────────────────────

    [Fact]
    public async Task Get_forecast_returns_400_when_no_accounts_exist()
    {
        // The handler throws ForecastInputException("No active accounts found...")
        // which the endpoint maps to 400. This is by-design, not a bug.
        var response = await _client.GetAsync("/api/forecast");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 200 with seeded data ──────────────────────────────────────────────────

    [Fact]
    public async Task Get_forecast_returns_200_and_required_fields_after_sync()
    {
        // Seed via connect+sync so the engine has accounts.
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var response = await _client.GetAsync("/api/forecast?months=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("forecastRunId", out _).Should().BeTrue("forecastRunId must be present");
        root.TryGetProperty("asOf", out _).Should().BeTrue("asOf must be present");
        root.TryGetProperty("accountSeries", out _).Should().BeTrue("accountSeries must be present");
        root.TryGetProperty("aggregateSeries", out _).Should().BeTrue("aggregateSeries must be present");
        root.TryGetProperty("lowWaterMarks", out _).Should().BeTrue("lowWaterMarks must be present");
        root.TryGetProperty("overdraftWarnings", out _).Should().BeTrue("overdraftWarnings must be present");
        root.TryGetProperty("goalOutcomes", out _).Should().BeTrue("goalOutcomes must be present");
    }

    [Fact]
    public async Task Get_forecast_after_sync_includes_non_empty_account_series()
    {
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var response = await _client.GetAsync("/api/forecast?months=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var accountSeries = doc.RootElement.GetProperty("accountSeries");
        accountSeries.ValueKind.Should().Be(JsonValueKind.Array);
        accountSeries.GetArrayLength().Should().BeGreaterThan(0, "sync seeds at least one account");
    }

    [Fact]
    public async Task Get_forecast_with_explicit_date_range_returns_200()
    {
        // Need at least one account for the forecast to succeed.
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var from = DateOnly.FromDateTime(DateTime.UtcNow);
        var to = from.AddMonths(2);
        var url = $"/api/forecast?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

        var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Unauthenticated ───────────────────────────────────────────────────────

    [Fact]
    public async Task Get_forecast_without_auth_returns_401()
    {
        using var anon = factory.CreateClient();
        var response = await anon.GetAsync("/api/forecast");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
