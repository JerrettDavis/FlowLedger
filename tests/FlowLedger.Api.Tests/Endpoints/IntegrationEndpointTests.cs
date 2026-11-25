using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>Pins the /api/integrations contract: validated URLs, write-only secrets, signing secret shown once, tenant isolation.</summary>
[Collection("ApiIntegration")]
public sealed class IntegrationEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private const string Token = "tok-VerySecret-123";
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Ad-hoc tables are created lazily, so isolate by tenant instead of resetting.
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient(Guid.NewGuid());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object Body(string kind, string url, string name = "Thing", string auth = "None", string? credential = null, string? user = null, string[]? events = null)
        => new { name, kind, enabled = true, url, auth, authUser = user, credential, clearCredential = false, headers = (object?)null, events, soapEndpoint = (string?)null };

    [Fact]
    public async Task Rest_integration_is_created_listed_and_never_returns_its_credential()
    {
        var create = await _client.PostAsJsonAsync("/api/integrations", Body("Rest", "https://api.example.com/v1", auth: "Bearer", credential: Token));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadAsStringAsync();
        created.Should().NotContain(Token);

        var list = await _client.GetStringAsync("/api/integrations");
        list.Should().NotContain(Token);
        using var doc = JsonDocument.Parse(list);
        var item = doc.RootElement.GetProperty("integrations").EnumerateArray().Single();
        item.GetProperty("kind").GetString().Should().Be("Rest");
        item.GetProperty("hasCredential").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("canStoreSecrets").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("http://api.example.com")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("https://user:pw@api.example.com")]
    public async Task Unsafe_urls_are_rejected(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/integrations", Body("Rest", url));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("url");
    }

    [Fact]
    public async Task Webhook_signing_secret_is_returned_once_and_can_be_rotated()
    {
        var create = await _client.PostAsJsonAsync("/api/integrations", Body("Webhook", "https://hooks.example.com/in", events: ["import.completed"]));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var secret = created.RootElement.GetProperty("signingSecret").GetString();
        secret.Should().StartWith("whsec_");
        var id = created.RootElement.GetProperty("integration").GetProperty("id").GetGuid();

        (await _client.GetStringAsync("/api/integrations")).Should().NotContain(secret!);

        var rotate = await _client.PostAsync($"/api/integrations/{id}/rotate-secret", null);
        rotate.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rotated = JsonDocument.Parse(await rotate.Content.ReadAsStringAsync());
        rotated.RootElement.GetProperty("signingSecret").GetString().Should().NotBe(secret).And.StartWith("whsec_");
    }

    [Fact]
    public async Task A_rest_call_to_an_unreachable_host_is_reported_not_thrown_and_logged()
    {
        var create = await _client.PostAsJsonAsync("/api/integrations", Body("Rest", "https://nonexistent.invalid/v1"));
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("integration").GetProperty("id").GetGuid();

        var call = await _client.PostAsJsonAsync($"/api/integrations/{id}/rest/call", new { method = "GET", path = "/ping" });
        call.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();

        var log = await _client.GetStringAsync($"/api/integrations/{id}/deliveries");
        log.Should().Contain("nonexistent.invalid/v1/ping");
    }

    [Fact]
    public async Task Integrations_are_isolated_per_tenant_and_can_be_deleted()
    {
        var create = await _client.PostAsJsonAsync("/api/integrations", Body("Rest", "https://api.example.com/v1"));
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("integration").GetProperty("id").GetGuid();

        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        using var theirs = JsonDocument.Parse(await other.GetStringAsync("/api/integrations"));
        theirs.RootElement.GetProperty("integrations").GetArrayLength().Should().Be(0);
        (await other.DeleteAsync($"/api/integrations/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _client.DeleteAsync($"/api/integrations/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var mine = JsonDocument.Parse(await _client.GetStringAsync("/api/integrations"));
        mine.RootElement.GetProperty("integrations").GetArrayLength().Should().Be(0);
    }
}
