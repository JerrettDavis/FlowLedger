using System.Net;
using System.Net.Http.Json;
using System.Text;
using FlowLedger.Integrations.Simulated;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>
/// Tests for MX-specific endpoints.
/// Mx:Enabled=false in the test factory, so IFinancialDataProvider resolves to Simulated.
/// </summary>
[Collection("ApiIntegration")]
public sealed class MxEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Connect token (Mx disabled → 409) ─────────────────────────────────────

    [Fact]
    public async Task Post_connect_token_returns_409_when_mx_disabled()
    {
        // The factory sets Mx:Enabled=false, so the provider is Simulated, not MxFinancialDataProvider.
        // The endpoint checks `provider is not MxFinancialDataProvider` and returns 409.
        var response = await _client.PostAsync("/api/integrations/mx/connect-token", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Post_connect_token_without_auth_returns_401()
    {
        using var anon = factory.CreateClient();
        var response = await anon.PostAsync("/api/integrations/mx/connect-token", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Webhook (anonymous, HMAC-validated) ───────────────────────────────────

    [Fact]
    public async Task Post_webhook_with_invalid_signature_returns_401_without_www_authenticate()
    {
        // The webhook endpoint is AllowAnonymous — auth layer passes the request through.
        // The HMAC check fails → 401, but without WWW-Authenticate (it's not a bearer challenge).
        using var anon = factory.CreateClient();
        anon.DefaultRequestHeaders.Add("X-MX-Signature", "bad-signature");

        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await anon.PostAsync("/api/integrations/mx/webhooks", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("WWW-Authenticate").Should().BeFalse(
            "a 401 from HMAC check should not carry a WWW-Authenticate challenge");
    }

    [Fact]
    public async Task Post_webhook_without_signature_header_returns_401()
    {
        using var anon = factory.CreateClient();

        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await anon.PostAsync("/api/integrations/mx/webhooks", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_webhook_with_valid_signature_but_malformed_json_returns_400()
    {
        // The factory uses Mx:Enabled=false → Simulated provider.
        // The Simulated provider verifies HMAC against its test secret, then parses the JSON.
        // Malformed JSON causes JsonException inside ParseWebhookAsync; the endpoint must
        // translate that into 400 Bad Request rather than letting it propagate as a 500.
        using var anon = factory.CreateClient();

        var badJson = Encoding.UTF8.GetBytes("this is { not valid ] json at all");
        var signature = SimulatedFinancialDataProvider.BuildTestSignature(badJson);

        anon.DefaultRequestHeaders.Add("X-MX-Signature", signature);
        var content = new ByteArrayContent(badJson);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await anon.PostAsync("/api/integrations/mx/webhooks", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a syntactically invalid webhook payload must return 400, not 500");
    }
}
