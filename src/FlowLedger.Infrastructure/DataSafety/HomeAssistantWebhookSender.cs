using System.Net.Http.Json;
using FlowLedger.Application.Features.HomeAssistant;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// POSTs an event to a Home Assistant webhook. Redirects are not followed (a redirect could carry the secret URL somewhere
/// else), the timeout is short, the URL is re-validated on every send, and failures are reduced to a short safe summary:
/// neither the URL nor the response body is ever logged or stored.
/// </summary>
public sealed class HomeAssistantWebhookSender(IHttpClientFactory http) : IHomeAssistantSender
{
    public const string HttpClientName = "home-assistant";

    public async Task<string?> SendAsync(string webhookUrl, object payload, CancellationToken ct = default)
    {
        if (!HomeAssistantWebhookUrl.TryValidate(webhookUrl, out var url, out _))
        {
            return "The stored webhook URL is no longer valid. Save it again.";
        }

        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(url, payload, ct);
            return response.IsSuccessStatusCode ? null : $"Home Assistant answered HTTP {(int)response.StatusCode}.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Home Assistant did not answer in time.";
        }
        catch (HttpRequestException)
        {
            return "Could not reach Home Assistant.";
        }
    }
}
