using System.Net;
using System.Net.Http.Json;
using System.Text;
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
}
