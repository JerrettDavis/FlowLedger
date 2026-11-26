using System.Net;
using System.Net.Http.Json;
using System.Text;
using FlowLedger.Integrations.Simulated;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

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
        var signature = SimulatedFinancialDataProvider.BuildSignature(badJson, FlowLedgerApiFactory.WebhookSecret);

        anon.DefaultRequestHeaders.Add("X-MX-Signature", signature);
        var content = new ByteArrayContent(badJson);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await anon.PostAsync("/api/integrations/mx/webhooks", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a syntactically invalid webhook payload must return 400, not 500");
    }

    [Fact]
    public async Task Post_webhook_signed_with_the_old_public_secret_is_rejected_when_no_secret_is_configured()
    {
        // #133: with default config (no SimulatedProvider:WebhookSecret) the anonymous webhook must not
        // verify anything, even a signature built from the secret that used to be hard-coded in the repo.
        using var noSecret = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["SimulatedProvider:WebhookSecret"] = "" })));
        using var anon = noSecret.CreateClient();

        var body = Encoding.UTF8.GetBytes("{\"event_type\":\"SYNC_COMPLETE\",\"member_id\":\"sim-member-001\"}");
        anon.DefaultRequestHeaders.Add("X-MX-Signature",
            SimulatedFinancialDataProvider.BuildSignature(body, "sim-test-webhook-secret-do-not-use-in-production"));
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await anon.PostAsync("/api/integrations/mx/webhooks", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Webhook body cap (anonymous endpoint: bound memory before HMAC work) ──

    private const int WebhookCap = 1024 * 1024;

    private static HttpContent Body(byte[] bytes, bool chunked)
    {
        HttpContent content = chunked ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_webhook_over_1MB_returns_413_even_with_a_valid_signature(bool chunked)
    {
        using var anon = factory.CreateClient();
        var big = new byte[WebhookCap + 1];
        Array.Fill(big, (byte)'a');
        anon.DefaultRequestHeaders.Add("X-MX-Signature", SimulatedFinancialDataProvider.BuildSignature(big, FlowLedgerApiFactory.WebhookSecret));

        var response = await anon.PostAsync("/api/integrations/mx/webhooks", Body(big, chunked));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Post_webhook_of_exactly_1MB_is_still_processed()
    {
        using var anon = factory.CreateClient();
        var body = new byte[WebhookCap];
        Array.Fill(body, (byte)'a');
        anon.DefaultRequestHeaders.Add("X-MX-Signature", SimulatedFinancialDataProvider.BuildSignature(body, FlowLedgerApiFactory.WebhookSecret));

        var response = await anon.PostAsync("/api/integrations/mx/webhooks", Body(body, chunked: true));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "verified, then rejected as invalid JSON; not capped");
    }

    /// <summary>Forces chunked transfer (no Content-Length) so the server cannot rely on the header.</summary>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
