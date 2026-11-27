using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using FlowLedger.Application.Features.HomeAssistant;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.Outbound;

/// <summary>
/// POSTs an event to a Home Assistant webhook. Redirects are not followed (a redirect could carry the secret URL somewhere
/// else), the timeout is short, the URL is re-validated on every send, the destination is checked again at connect time
/// (<see cref="CreateHandler"/>), and failures are reduced to a short safe summary: neither the URL nor the response body is
/// ever logged or stored.
/// </summary>
internal sealed class HomeAssistantWebhookSender(IHttpClientFactory http) : IHomeAssistantSender
{
    public const string HttpClientName = "home-assistant";

    internal static readonly HttpRequestOptionsKey<bool> AllowLocalKey = new("flowledger-ha-allow-local");

    public async Task<string?> SendAsync(string webhookUrl, object payload, bool allowLocalNetwork, CancellationToken ct = default)
    {
        if (!HomeAssistantWebhookUrl.TryValidate(webhookUrl, allowLocalNetwork, out var url, out _))
        {
            return "The stored webhook URL is no longer valid. Save it again.";
        }

        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            request.Options.Set(AllowLocalKey, allowLocalNetwork);
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : $"Home Assistant answered HTTP {(int)response.StatusCode}.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Home Assistant did not answer in time.";
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or InvalidOperationException)
        {
            return "Could not reach Home Assistant.";
        }
    }

    /// <summary>
    /// The handler used for every send: no redirects, and the connection is made by us. The host is resolved here and the socket is
    /// pinned to a checked address, so a public-looking name that resolves (or later re-resolves, DNS rebinding) to a link-local,
    /// multicast or, unless the tenant is the household owner, loopback/private address is refused. TLS still validates the
    /// original host name.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            var allowLocal = context.InitialRequestMessage?.Options.TryGetValue(AllowLocalKey, out var v) == true && v;
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var chosen = addresses.FirstOrDefault(a =>
                    HomeAssistantWebhookUrl.Classify(a) is var k
                    && (k == HomeAssistantWebhookUrl.NetworkKind.Public || (k == HomeAssistantWebhookUrl.NetworkKind.Local && allowLocal)))
                ?? throw new HttpRequestException("Destination address is not allowed.");

            var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(chosen, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>Only the household (operator) tenant may target local-network addresses; every other tenant is public-https only.</summary>
internal sealed class HouseholdHomeAssistantNetworkPolicy(IConfiguration configuration) : IHomeAssistantNetworkPolicy
{
    public bool AllowLocalNetwork(Guid tenantId) => tenantId == TenancySettings.Legacy(configuration).LegacyTenantId;
}
