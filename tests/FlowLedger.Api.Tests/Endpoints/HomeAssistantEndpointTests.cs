using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>Pins the /api/home-assistant contract: alerts-only defaults, validated and write-only webhook URL.</summary>
[Collection("ApiIntegration")]
public sealed class HomeAssistantEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private const string Secret = "AbCdEf0123456789XyZ";
    private const string Url = "https://ha.example.com/api/webhook/" + Secret;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Ad-hoc tables are created lazily (after Respawn snapshots its table list), so isolate by tenant instead of resetting.
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient(Guid.NewGuid());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object Body(bool enabled, string? url = null, bool amounts = false) => new
    {
        enabled, webhookUrl = url, clearWebhook = false, shareAmounts = amounts, includeNames = true, leadDays = 3, kinds = (string[]?)null,
    };

    [Fact]
    public async Task Defaults_are_off_and_share_no_amounts()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/home-assistant"));
        var s = doc.RootElement.GetProperty("settings");
        s.GetProperty("enabled").GetBoolean().Should().BeFalse();
        s.GetProperty("shareAmounts").GetBoolean().Should().BeFalse();
        s.GetProperty("hasWebhook").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("kinds").GetArrayLength().Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task Cannot_enable_without_a_webhook()
    {
        var response = await _client.PutAsJsonAsync("/api/home-assistant", Body(true));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("http://ha.example.com/api/webhook/" + Secret)]
    [InlineData("https://ha.example.com/api/webhook/short")]
    [InlineData("https://u:p@ha.example.com/api/webhook/" + Secret)]
    [InlineData("http://169.254.169.254/api/webhook/" + Secret)]
    [InlineData("https://[::ffff:169.254.169.254]/api/webhook/" + Secret)]
    [InlineData("http://192.168.1.5:8123/api/webhook/" + Secret)]
    [InlineData("http://redis:6379/api/webhook/" + Secret)]
    [InlineData("http://localhost:5432/api/webhook/" + Secret)]
    [InlineData("https://[::1]/api/webhook/" + Secret)]
    public async Task Rejects_unsafe_webhook_urls(string url)
    {
        var response = await _client.PutAsJsonAsync("/api/home-assistant", Body(true, url));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Secret);
    }

    [Fact]
    public async Task Only_the_household_owner_may_target_the_local_network()
    {
        const string lan = "http://homeassistant.local:8123/api/webhook/" + Secret;
        (await _client.PutAsJsonAsync("/api/home-assistant", Body(true, lan))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var household = factory.CreateAuthenticatedClient(); // the demo tenant is the household (legacy) tenant
        (await household.PutAsJsonAsync("/api/home-assistant", Body(false, lan))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Saved_webhook_is_write_only()
    {
        var put = await _client.PutAsJsonAsync("/api/home-assistant", Body(true, Url));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await put.Content.ReadAsStringAsync()).Should().NotContain(Secret);

        var get = await _client.GetStringAsync("/api/home-assistant");
        get.Should().NotContain(Secret);
        using var doc = JsonDocument.Parse(get);
        var s = doc.RootElement.GetProperty("settings");
        s.GetProperty("enabled").GetBoolean().Should().BeTrue();
        s.GetProperty("hasWebhook").GetBoolean().Should().BeTrue();
        s.GetProperty("webhookHost").GetString().Should().Be("ha.example.com");
        s.GetProperty("shareAmounts").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Preview_never_includes_numbers()
    {
        var response = await _client.GetAsync("/api/home-assistant/preview");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("\"amount\"").And.NotContain("\"balance\"");
    }

    [Fact]
    public async Task Test_without_a_webhook_fails_cleanly()
    {
        var response = await _client.PostAsync("/api/home-assistant/test", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Send_without_sharing_enabled_sends_nothing()
    {
        var response = await _client.PostAsync("/api/home-assistant/send", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("sent").GetInt32().Should().Be(0);
    }
}
