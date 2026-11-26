using System.Net;
using System.Text;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// HTTP-level negative tests for the anonymous webhook endpoints (#158): they must reject bad input without ever presenting an
/// auth challenge, and must bound the body before doing signature work.
/// </summary>
[Collection("ApiIntegration")]
public sealed class WebhookNegativeTests(FlowLedgerApiFactory factory)
{
    private const string Mx = "/api/integrations/mx/webhooks";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Mx_webhook_with_a_bad_signature_is_401_without_an_auth_challenge()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-MX-Signature", "00ff");

        var response = await client.PostAsync(Mx, Json("{\"type\":\"x\"}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("WWW-Authenticate").Should().BeFalse();
    }

    [Fact]
    public async Task Mx_webhook_without_a_signature_is_401()
    {
        var response = await factory.CreateClient().PostAsync(Mx, Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mx_webhook_with_a_valid_api_key_but_no_signature_is_still_401()
    {
        // The API key is not a substitute for the provider signature on this endpoint.
        var response = await factory.CreateAuthenticatedClient().PostAsync(Mx, Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mx_webhook_declaring_an_oversize_body_is_413_before_any_signature_work()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-MX-Signature", "00ff");
        using var content = new ByteArrayContent(new byte[(1024 * 1024) + 1]);
        content.Headers.ContentType = new("application/json");

        var response = await client.PostAsync(Mx, content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Mx_webhook_streaming_an_oversize_body_without_content_length_is_413()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-MX-Signature", "00ff");
        using var content = new StreamContent(new MemoryStream(new byte[(1024 * 1024) + 1]));
        content.Headers.ContentType = new("application/json");
        client.DefaultRequestHeaders.TransferEncodingChunked = true;

        var response = await client.PostAsync(Mx, content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Quiltt_webhook_is_anonymous_and_does_not_leak_when_the_connector_is_off()
    {
        var response = await factory.CreateClient().PostAsync("/api/integrations/quiltt/webhooks", Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Contains("WWW-Authenticate").Should().BeFalse();
    }
}
