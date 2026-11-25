using System.Net.Sockets;
using System.Text;
using FlowLedger.Application.Features.Integrations;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// The one outbound HTTP path for integrations. It reuses the Home Assistant connect guard (<see cref="HomeAssistantWebhookSender.CreateHandler"/>):
/// no redirects, a short connect timeout, and the destination is resolved and pinned at connect time so a public-looking name that
/// resolves to an internal address is refused. Responses are read with a hard size cap, and failures are reduced to a short fixed
/// string (never the URL, headers or credentials).
/// </summary>
public sealed class IntegrationTransport(IHttpClientFactory http) : IIntegrationTransport
{
    public const string HttpClientName = "integrations";

    /// <summary>A separate client (and so a separate connection pool) for the household owner, who may reach local addresses.
    /// A pooled connection opened under that policy must never be reused for another tenant, because the connect-time check
    /// only runs for new connections.</summary>
    public const string LocalHttpClientName = "integrations-local";
    public const int MaxResponseBytes = 256 * 1024;

    public async Task<TransportResponse> SendAsync(TransportRequest request, bool allowLocalNetwork, CancellationToken ct = default)
    {
        try
        {
            using var client = http.CreateClient(allowLocalNetwork ? LocalHttpClientName : HttpClientName);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            message.Options.Set(HomeAssistantWebhookSender.AllowLocalKey, allowLocalNetwork);
            if (request.Body is not null)
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8);
                message.Content.Headers.Remove("Content-Type");
                message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType ?? "application/json");
            }

            foreach (var (name, value) in request.Headers)
            {
                if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var (body, truncated) = await ReadCappedAsync(response.Content, ct);
            return new TransportResponse((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), body, truncated, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TransportResponse(null, null, "", false, "The service did not answer in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or InvalidOperationException or FormatException)
        {
            return new TransportResponse(null, null, "", false, "Could not reach the service.");
        }
    }

    private static async Task<(string Body, bool Truncated)> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var truncated = total > MaxResponseBytes;
        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(total, MaxResponseBytes)), truncated);
    }
}
