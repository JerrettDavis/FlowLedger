using System.Net;
using System.Text;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// #94: the MX and Quiltt webhook endpoints share one pipeline (WebhookEndpointBuilder), so the provider-agnostic outcomes must
/// be identical for both. Runs against the connector-probe host, where Quiltt is enabled alongside the default connector.
/// </summary>
[Collection("ApiIntegration")]
public sealed class WebhookPipelineParityTests(ConnectorProbeApiFactory factory)
{
    public static TheoryData<string, string, string> Providers => new()
    {
        { "mx", "/api/integrations/mx/webhooks", "X-MX-Signature" },
        { "quiltt", "/api/integrations/quiltt/webhooks", "Quiltt-Signature" },
    };

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_bad_signature_is_401_without_an_auth_challenge(string provider, string route, string header)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(header, "00ff");
        client.DefaultRequestHeaders.Add("Quiltt-Timestamp", "1700000000");

        var response = await client.PostAsync(route, Json("{\"type\":\"x\"}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, provider);
        response.Headers.Contains("WWW-Authenticate").Should().BeFalse(provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task An_oversize_body_is_413_before_signature_work(string provider, string route, string header)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(header, "00ff");
        using var content = new ByteArrayContent(new byte[(1024 * 1024) + 1]);
        content.Headers.ContentType = new("application/json");

        var response = await client.PostAsync(route, content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_valid_api_key_is_not_a_substitute_for_the_signature(string provider, string route, string header)
    {
        _ = header;
        var response = await factory.CreateAuthenticatedClient().PostAsync(route, Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, provider);
    }
}
