using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class ForecastBandsEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Forecast_returns_bands_and_spending_profile_once_there_are_enough_weeks_of_spending()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts",
            CreateAccountRequestBuilder.A().Named("Checking").StartingAt(5000m).Build())).Content.ReadFromJsonAsync<AccountDto>())!;

        var today = factory.ServerToday;
        for (var week = 1; week <= 12; week++)
        {
            var date = today.AddDays(-7 * week + 1);
            var amount = 80m + week % 4 * 25m;
            var response = await _client.PostAsJsonAsync("/api/transactions",
                CreateTransactionRequestBuilder.A().OnAccount(account.Id).WithAmount(amount).Described($"Corner Grocer {week}").On(date).PostedOn(date).AtMerchant($"Corner Grocer {week}").Build());
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var forecast = await _client.GetAsync("/api/forecast?months=2");

        forecast.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await forecast.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("spendHistoryWeeks").GetInt32().Should().BeGreaterThanOrEqualTo(8);
        root.GetProperty("typicalWeeklySpend").GetDecimal().Should().BeGreaterThan(0m);
        var bands = root.GetProperty("bands");
        bands.GetArrayLength().Should().BeGreaterThan(0);
        var last = bands[bands.GetArrayLength() - 1];
        last.GetProperty("low").GetDecimal().Should().BeLessThan(last.GetProperty("expected").GetDecimal());
        last.GetProperty("high").GetDecimal().Should().BeGreaterThan(last.GetProperty("expected").GetDecimal());
    }

    [Fact]
    public async Task Forecast_without_spending_history_has_empty_bands_but_still_succeeds()
    {
        await _client.PostAsJsonAsync("/api/accounts", CreateAccountRequestBuilder.A().Named("Checking").StartingAt(1000m).Build());

        var forecast = await _client.GetAsync("/api/forecast?months=1");

        forecast.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await forecast.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("bands").GetArrayLength().Should().Be(0);
    }
}
