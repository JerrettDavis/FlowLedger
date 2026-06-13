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

    // ── Wire contract: accountSeries[].accountId must be a bare GUID ──────────

    [Fact]
    public async Task Get_forecast_response_deserializes_into_web_client_dto_without_jsonexception()
    {
        // REGRESSION (Dashboard bug): the API used to return the domain ForecastResult
        // directly, so accountSeries[0].accountId serialized as {"value":"<guid>"} and the
        // Web client's deserialization into Guid threw:
        //   "The JSON value could not be converted to System.Guid.
        //    Path: $.accountSeries[0].accountId"
        // This deserializes the LIVE response into a DTO mirroring the Web client's
        // ForecastResultDto (AccountId : Guid) using the same options the client uses.
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var response = await _client.GetAsync("/api/forecast?months=3");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Must NOT throw a JsonException — this is the exact failure path the Dashboard hit.
        var act = () => JsonSerializer.Deserialize<WireForecastResult>(json, options);
        var dto = act.Should().NotThrow().Subject;

        dto.Should().NotBeNull();
        dto!.AccountSeries.Should().NotBeEmpty("sync seeds at least one account");
        dto.AccountSeries.Should().OnlyContain(
            s => s.AccountId != Guid.Empty,
            "every accountId must be a real, bare GUID — not a nested object or empty");
        // The horizon must be flattened, not a nested range object.
        dto.HorizonStart.Should().NotBe(default);
        dto.HorizonEnd.Should().BeOnOrAfter(dto.HorizonStart);
    }

    [Fact]
    public async Task Get_forecast_account_series_accountId_is_json_string_not_object()
    {
        // Guards the wire shape directly: accountId must be a STRING token (GUID),
        // never a JSON object like {"value":"..."}.
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);

        var response = await _client.GetAsync("/api/forecast?months=3");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.GetProperty("accountSeries")[0];
        var accountId = first.GetProperty("accountId");

        accountId.ValueKind.Should().Be(JsonValueKind.String,
            "accountId must serialize as a bare GUID string, not a nested object");
        Guid.TryParse(accountId.GetString(), out _).Should().BeTrue(
            "accountId string must parse as a Guid");
    }

    /// <summary>
    /// Mirror of the Web client's <c>FlowLedger.Web.ApiClient.ForecastResultDto</c> /
    /// <c>AccountForecastSeriesDto</c> (Api.Tests cannot reference the Web project).
    /// AccountId is a plain <see cref="Guid"/> — the type that broke before the fix.
    /// </summary>
    private sealed class WireForecastResult
    {
        public Guid ForecastRunId { get; init; }
        public DateOnly AsOf { get; init; }
        public DateOnly HorizonStart { get; init; }
        public DateOnly HorizonEnd { get; init; }
        public List<WireAccountSeries> AccountSeries { get; init; } = [];
    }

    private sealed class WireAccountSeries
    {
        public Guid AccountId { get; init; }
        public decimal StartingBalanceAmount { get; init; }
        public string StartingBalanceCurrency { get; init; } = "USD";
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
