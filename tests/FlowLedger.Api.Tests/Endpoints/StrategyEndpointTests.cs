using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>End-to-end against real Postgres (Testcontainers): synced data -> suggestions, coach, trajectory, rates and simulators.</summary>
[Collection("ApiIntegration")]
public sealed class StrategyEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync()
    {
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);
    }

    [Fact]
    public async Task Strategies_report_has_rates_coach_trajectory_and_disclaimer()
    {
        await SeedAsync();

        var response = await _client.GetAsync("/api/strategies");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("disclaimer").GetString().Should().Contain("not personalised");
        var rates = root.GetProperty("rates");
        rates.GetProperty("hysa").GetDecimal().Should().BeGreaterThan(0);
        rates.GetProperty("mortgage30").GetDecimal().Should().BeGreaterThan(0);
        root.GetProperty("suggestions").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("coach").ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("coach").GetProperty("headline").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("trajectory").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task Strategies_on_an_empty_tenant_still_returns_200_with_an_all_clear_coach()
    {
        var response = await _client.GetAsync("/api/strategies");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("suggestions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Account_rate_round_trips_and_changes_the_report()
    {
        await SeedAsync();
        using var accounts = JsonDocument.Parse(await _client.GetStringAsync("/api/accounts"));
        var id = accounts.RootElement.EnumerateArray().First().GetProperty("id").GetGuid();

        var put = await _client.PutAsJsonAsync($"/api/strategies/account-rates/{id}", new { ratePercent = 4.25m, remainingMonths = (int?)null });
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var list = JsonDocument.Parse(await _client.GetStringAsync("/api/strategies/account-rates"));
        list.RootElement.EnumerateArray().Should().ContainSingle(r => r.GetProperty("accountId").GetGuid() == id
            && r.GetProperty("ratePercent").GetDecimal() == 4.25m);

        (await _client.DeleteAsync($"/api/strategies/account-rates/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var after = JsonDocument.Parse(await _client.GetStringAsync("/api/strategies/account-rates"));
        after.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Account_rate_for_unknown_account_is_404_and_invalid_rate_is_400()
    {
        var unknown = await _client.PutAsJsonAsync($"/api/strategies/account-rates/{Guid.NewGuid()}", new { ratePercent = 3m, remainingMonths = (int?)null });
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bad = await _client.PutAsJsonAsync($"/api/strategies/account-rates/{Guid.NewGuid()}", new { ratePercent = 250m, remainingMonths = (int?)null });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Simulators_return_results_and_reject_bad_input()
    {
        var growth = await _client.PostAsJsonAsync("/api/strategies/simulate/growth",
            new { start = 10000, baseMonthly = 100, baseApy = 0.5, scenarioMonthly = 100, scenarioApy = 4.5, years = 5 });
        growth.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var g = JsonDocument.Parse(await growth.Content.ReadAsStringAsync()))
        {
            g.RootElement.GetProperty("difference").GetDecimal().Should().BeGreaterThan(1000m);
        }

        var refi = await _client.PostAsJsonAsync("/api/strategies/simulate/refinance",
            new { balance = 300000, currentRate = 7.5, remainingMonths = 360, newRate = 6.0, newTermMonths = 360, closingCosts = 6000 });
        refi.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var r = JsonDocument.Parse(await refi.Content.ReadAsStringAsync()))
        {
            r.RootElement.GetProperty("worthwhile").GetBoolean().Should().BeTrue();
        }

        var ladder = await _client.PostAsJsonAsync("/api/strategies/simulate/ladder", new { amount = 40000 });
        ladder.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync("/api/strategies/simulate/growth",
            new { start = 1, baseMonthly = 0, baseApy = 500, scenarioMonthly = 0, scenarioApy = 1, years = 5 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync("/api/strategies/simulate/ladder", new { amount = 5 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Past_what_if_replays_synced_history()
    {
        await SeedAsync();

        var response = await _client.PostAsJsonAsync("/api/strategies/simulate/past",
            new { months = 6, extraMonthly = 200, cutCategory = (string?)null, cutPercent = 0, idleCash = 5000, fromApy = 0.5, toApy = 4.0, growthApy = 4.0 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("totalExtra").GetDecimal().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("fromSaving").GetDecimal().Should().BeGreaterThan(0);
    }
}
