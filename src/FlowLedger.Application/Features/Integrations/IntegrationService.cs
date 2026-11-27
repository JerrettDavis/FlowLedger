using System.Diagnostics;
using System.Text.Json;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Integrations;

/// <summary>
/// The base integration layer: define connections to other services (REST APIs, SOAP/WSDL services, signed webhooks), call them
/// on demand, push FlowLedger events to subscribed webhooks, and keep a delivery log. Every outbound request goes through
/// <see cref="IIntegrationTransport"/> (SSRF-guarded, no redirects, capped response). Credentials and signing secrets are write-only:
/// no read model ever contains them.
/// </summary>
public sealed partial class IntegrationService(
    IIntegrationStore store,
    IIntegrationTransport transport,
    IHomeAssistantNetworkPolicy network,
    ITenantContext tenant,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public const int MaxIntegrationsPerTenant = 25;
    public const int MaxHeaders = 10;
    public const int MaxRequestBytes = 256 * 1024;
    public static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(8);

    public const string SignatureHeader = "X-FlowLedger-Signature";
    public const string TimestampHeader = "X-FlowLedger-Timestamp";
    public const string EventHeader = "X-FlowLedger-Event";
    public const string DeliveryHeader = "X-FlowLedger-Delivery";

    private static readonly HashSet<string> RestMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Guid TenantId => tenant.TenantId;

    private bool AllowLocal => network.AllowLocalNetwork(TenantId);

    public bool CanStoreSecrets => store.CanStoreSecrets;

    /// <summary>The <see cref="SignatureHeader"/> value for a delivery: <c>sha256=hex(HMAC-SHA256(secret, timestamp + "." + body))</c>.</summary>
    public static string Sign(string secret, string timestamp, string body) => WebhookSigning.Sign(secret, timestamp, body);

    public Task<IReadOnlyList<Integration>> ListAsync(CancellationToken ct = default) => store.ListAsync(TenantId, ct);

    public Task<Integration?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAsync(TenantId, id, ct);

    public Task<IReadOnlyList<IntegrationDelivery>> DeliveriesAsync(Guid id, CancellationToken ct = default)
        => store.ListDeliveriesAsync(TenantId, id, 50, ct);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await store.DeleteAsync(TenantId, id, ct);

    private async Task<IntegrationCallResult> SendAndLogAsync(Integration integration, string trigger, TransportRequest request, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var response = await transport.SendAsync(request, AllowLocal, ct);
        watch.Stop();
        var error = response.Error ?? (response.StatusCode is { } code and >= 400 ? $"The service answered HTTP {code}." : null);
        var result = new IntegrationCallResult(response.StatusCode, response.ContentType, response.Body, response.Truncated, (int)watch.ElapsedMilliseconds, error);
        try
        {
            await store.RecordDeliveryAsync(
                TenantId,
                new IntegrationDelivery(Guid.NewGuid(), integration.Id, _clock.GetUtcNow(), trigger, request.Method,
                    IntegrationUrl.SafeTarget(request.Url), response.StatusCode, result.DurationMs, error),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logging a delivery must never mask the call's own result.
        }

        return result;
    }
}
