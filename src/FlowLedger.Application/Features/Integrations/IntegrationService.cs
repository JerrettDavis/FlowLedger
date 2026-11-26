using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowLedger.Application.Abstractions;
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

    /// <summary>Headers the caller may not set: they would let a definition spoof routing, framing or the authentication we add.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "transfer-encoding", "connection", "upgrade", "te", "trailer", "expect",
        "authorization", "proxy-authorization", "cookie", "set-cookie", "soapaction",
        SignatureHeader, TimestampHeader, EventHeader, DeliveryHeader,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$")]
    private static partial Regex HeaderName();

    private Guid TenantId => tenant.TenantId;

    private bool AllowLocal => network.AllowLocalNetwork(TenantId);

    public bool CanStoreSecrets => store.CanStoreSecrets;

    public Task<IReadOnlyList<Integration>> ListAsync(CancellationToken ct = default) => store.ListAsync(TenantId, ct);

    public Task<Integration?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAsync(TenantId, id, ct);

    public Task<IReadOnlyList<IntegrationDelivery>> DeliveriesAsync(Guid id, CancellationToken ct = default)
        => store.ListDeliveriesAsync(TenantId, id, 50, ct);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await store.DeleteAsync(TenantId, id, ct);

    // ── Definitions ──────────────────────────────────────────────────────────────

    /// <summary>Creates (<paramref name="id"/> null) or updates an integration. A webhook's signing secret is returned once, on creation.</summary>
    public async Task<IntegrationSaveResult> SaveAsync(Guid? id, IntegrationSaveRequest r, CancellationToken ct = default)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > 80)
        {
            return IntegrationSaveResult.Fail("name", "Give the integration a name (up to 80 characters).");
        }

        if (!IntegrationUrl.TryValidate(r.Url, AllowLocal, out var url, out var urlError))
        {
            return IntegrationSaveResult.Fail("url", urlError!);
        }

        string? soapEndpoint = null;
        if (r.Kind == IntegrationKind.Soap && !string.IsNullOrWhiteSpace(r.SoapEndpoint))
        {
            if (!IntegrationUrl.TryValidate(r.SoapEndpoint, AllowLocal, out soapEndpoint, out var epError))
            {
                return IntegrationSaveResult.Fail("soapEndpoint", epError!);
            }
        }

        if (!TryCleanHeaders(r.Headers, out var headers, out var headerError))
        {
            return IntegrationSaveResult.Fail("headers", headerError!);
        }

        var authUser = r.AuthUser?.Trim();
        if (r.Auth == IntegrationAuth.ApiKey)
        {
            authUser = string.IsNullOrEmpty(authUser) ? "X-API-Key" : authUser;
            if (!HeaderName().IsMatch(authUser) || ReservedHeaders.Contains(authUser))
            {
                return IntegrationSaveResult.Fail("authUser", "Pick a valid header name for the API key (not Authorization or a reserved header).");
            }
        }
        else if (r.Auth == IntegrationAuth.Basic && string.IsNullOrEmpty(authUser))
        {
            return IntegrationSaveResult.Fail("authUser", "Enter the user name for basic authentication.");
        }
        else if (r.Auth != IntegrationAuth.Basic)
        {
            authUser = null;
        }

        var events = r.Kind == IntegrationKind.Webhook
            ? (r.Events ?? [.. IntegrationEvents.All]).Where(IntegrationEvents.All.Contains).Distinct().ToList()
            : [];
        if (r.Kind == IntegrationKind.Webhook && events.Count == 0)
        {
            return IntegrationSaveResult.Fail("events", "Choose at least one event to send.");
        }

        var existing = id is { } existingId ? await store.GetAsync(TenantId, existingId, ct) : null;
        if (id is not null && existing is null)
        {
            return IntegrationSaveResult.Fail("id", "That integration no longer exists.");
        }

        if (existing is null && (await store.ListAsync(TenantId, ct)).Count >= MaxIntegrationsPerTenant)
        {
            return IntegrationSaveResult.Fail("name", $"You can keep up to {MaxIntegrationsPerTenant} integrations. Remove one first.");
        }

        if (existing is not null && existing.Kind != r.Kind)
        {
            return IntegrationSaveResult.Fail("kind", "The type of an integration can't be changed. Create a new one instead.");
        }

        var credential = string.IsNullOrEmpty(r.Credential) ? null : r.Credential;
        if (r.Auth != IntegrationAuth.None && credential is null && (r.ClearCredential || existing is null || !existing.HasCredential))
        {
            return IntegrationSaveResult.Fail("credential", "Enter the credential (token, password or key) for the chosen authentication.");
        }

        if (credential is { Length: > 4096 })
        {
            return IntegrationSaveResult.Fail("credential", "That credential is too long.");
        }

        string? newSigning = null;
        if (r.Kind == IntegrationKind.Webhook && existing is null)
        {
            newSigning = NewSigningSecret();
        }

        if ((credential is not null || newSigning is not null) && !store.CanStoreSecrets)
        {
            return IntegrationSaveResult.Fail(
                "credential",
                "This server has no encryption key, so it will not store credentials or signing secrets. Ask the operator to set Integrations__EncryptionKey (base64, 32 bytes).");
        }

        var clearCredential = r.Auth == IntegrationAuth.None || (r.ClearCredential && credential is null);
        var hasCredential = r.Auth != IntegrationAuth.None && (credential is not null || (!clearCredential && existing?.HasCredential == true));
        var saved = new Integration(
            existing?.Id ?? Guid.NewGuid(), name, r.Kind, r.Enabled,
            new IntegrationConfig(url!, r.Auth, authUser, headers, events, soapEndpoint),
            hasCredential, existing?.HasSigningSecret == true || newSigning is not null,
            existing?.CreatedUtc ?? _clock.GetUtcNow(), existing?.LastUsedUtc, existing?.LastError);
        await store.SaveAsync(TenantId, saved, credential, clearCredential, newSigning, ct);
        return new IntegrationSaveResult(saved, null, newSigning);
    }

    /// <summary>Issues a fresh signing secret for a webhook, shown to the caller exactly once.</summary>
    public async Task<(Integration? Integration, string? Secret, string? Error)> RotateSigningSecretAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await store.GetAsync(TenantId, id, ct);
        if (existing is null || existing.Kind != IntegrationKind.Webhook)
        {
            return (null, null, "Only an existing webhook has a signing secret.");
        }

        if (!store.CanStoreSecrets)
        {
            return (null, null, "This server has no encryption key, so it will not store signing secrets.");
        }

        var secret = NewSigningSecret();
        var updated = existing with { HasSigningSecret = true };
        await store.SaveAsync(TenantId, updated, null, false, secret, ct);
        return (updated, secret, null);
    }

    // ── REST ─────────────────────────────────────────────────────────────────────

    public async Task<(IntegrationCallResult? Result, string? Error)> CallRestAsync(Guid id, RestCallRequest r, CancellationToken ct = default)
    {
        var integration = await store.GetAsync(TenantId, id, ct);
        if (integration is null || integration.Kind != IntegrationKind.Rest)
        {
            return (null, "That REST integration doesn't exist.");
        }

        if (!integration.Enabled)
        {
            return (null, "That integration is turned off.");
        }

        var method = (r.Method ?? "GET").Trim().ToUpperInvariant();
        if (!RestMethods.Contains(method))
        {
            return (null, "Use GET, POST, PUT, PATCH, DELETE or HEAD.");
        }

        if (!TryBuildRestUrl(integration.Config.Url, r.Path, r.Query, out var url, out var urlError))
        {
            return (null, urlError);
        }

        if (!TryCleanHeaders(r.Headers, out var extra, out var headerError))
        {
            return (null, headerError);
        }

        if (r.Body is { } body && Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
        {
            return (null, "The request body is too large (256 KB maximum).");
        }

        var secrets = await store.GetSecretsAsync(TenantId, id, ct);
        var headers = BuildHeaders(integration.Config, secrets, extra);
        var hasBody = !string.IsNullOrEmpty(r.Body) && method is not ("GET" or "HEAD");
        var result = await SendAndLogAsync(
            integration, "call", new TransportRequest(method, url!, headers, hasBody ? r.Body : null, hasBody ? (r.ContentType ?? "application/json") : null), ct);
        return (result, null);
    }

    // ── SOAP ─────────────────────────────────────────────────────────────────────

    /// <summary>Fetches the WSDL and lists what can be called.</summary>
    public async Task<SoapOperationsResult> SoapOperationsAsync(Guid id, CancellationToken ct = default)
    {
        var (integration, wsdl, error) = await LoadWsdlAsync(id, ct);
        return integration is null || wsdl is null
            ? new SoapOperationsResult([], error)
            : new SoapOperationsResult(wsdl.Operations, wsdl.Operations.Count == 0 ? "The WSDL has no SOAP operations." : null);
    }

    public async Task<(IntegrationCallResult? Result, string? Error)> SoapInvokeAsync(Guid id, SoapInvokeRequest r, CancellationToken ct = default)
    {
        var (integration, wsdl, error) = await LoadWsdlAsync(id, ct);
        if (integration is null || wsdl is null)
        {
            return (null, error);
        }

        var candidates = wsdl.Operations.Where(o => o.Name.Equals(r.Operation, StringComparison.Ordinal)).ToList();
        var op = candidates.FirstOrDefault(o => r.Version is null || o.SoapVersion == r.Version) ?? candidates.FirstOrDefault();
        if (op is null)
        {
            return (null, $"The WSDL has no operation named '{r.Operation}'.");
        }

        if (r.BodyXml is null || Encoding.UTF8.GetByteCount(r.BodyXml) > MaxRequestBytes)
        {
            return (null, "Provide the request body XML (256 KB maximum).");
        }

        string envelope;
        string contentType;
        try
        {
            (envelope, contentType) = SoapEnvelope.Build(op, r.BodyXml);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }

        var endpoint = integration.Config.SoapEndpoint ?? op.Endpoint;
        if (!IntegrationUrl.TryValidate(endpoint, AllowLocal, out var checkedEndpoint, out var endpointError))
        {
            return (null, "The service endpoint in the WSDL isn't allowed: " + endpointError);
        }

        var secrets = await store.GetSecretsAsync(TenantId, id, ct);
        var headers = BuildHeaders(integration.Config, secrets, new Dictionary<string, string>());
        if (op.SoapVersion != "1.2")
        {
            headers.Add(new KeyValuePair<string, string>("SOAPAction", "\"" + op.SoapAction.Replace("\"", "") + "\""));
        }

        var result = await SendAndLogAsync(
            integration, "soap:" + op.Name, new TransportRequest("POST", checkedEndpoint!, headers, envelope, contentType), ct);
        var fault = SoapEnvelope.FaultSummary(result.Body);
        return (fault is null ? result : result with { Error = fault }, null);
    }

    private async Task<(Integration? Integration, WsdlDescription? Wsdl, string? Error)> LoadWsdlAsync(Guid id, CancellationToken ct)
    {
        var integration = await store.GetAsync(TenantId, id, ct);
        if (integration is null || integration.Kind != IntegrationKind.Soap)
        {
            return (null, null, "That SOAP integration doesn't exist.");
        }

        if (!integration.Enabled)
        {
            return (null, null, "That integration is turned off.");
        }

        var secrets = await store.GetSecretsAsync(TenantId, id, ct);
        var headers = BuildHeaders(integration.Config, secrets, new Dictionary<string, string>());
        var response = await SendAndLogAsync(integration, "wsdl", new TransportRequest("GET", integration.Config.Url, headers, null, null), ct);
        if (response.Error is not null)
        {
            return (integration, null, response.Error);
        }

        try
        {
            return (integration, WsdlParser.Parse(response.Body), null);
        }
        catch (FormatException ex)
        {
            return (integration, null, ex.Message);
        }
    }

    // ── Webhooks ─────────────────────────────────────────────────────────────────

    /// <summary>Sends a harmless test event to a webhook so its receiver can be wired up.</summary>
    public async Task<(IntegrationCallResult? Result, string? Error)> TestWebhookAsync(Guid id, CancellationToken ct = default)
    {
        var integration = await store.GetAsync(TenantId, id, ct);
        if (integration is null || integration.Kind != IntegrationKind.Webhook)
        {
            return (null, "That webhook doesn't exist.");
        }

        var result = await DeliverAsync(integration, IntegrationEvents.Test, new Dictionary<string, object?> { ["message"] = "FlowLedger can reach this endpoint." }, ct);
        return (result, null);
    }

    /// <summary>
    /// Pushes an event to every enabled webhook subscribed to it. Never throws: a broken receiver must not break the action that
    /// raised the event; each outcome is in the delivery log instead.
    /// </summary>
    public async Task<int> DispatchAsync(string eventKind, object? data, CancellationToken ct = default)
    {
        var sent = 0;
        IReadOnlyList<Integration> all;
        try
        {
            all = await store.ListAsync(TenantId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }

        foreach (var integration in all)
        {
            if (integration.Kind != IntegrationKind.Webhook || !integration.Enabled || !integration.Config.Events.Contains(eventKind))
            {
                continue;
            }

            // A receiver that accepts the connection and never answers must not hold up the import or sync that raised the event.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(DispatchTimeout);
            try
            {
                await DeliverAsync(integration, eventKind, data, limit.Token);
                sent++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await RecordFailureAsync(integration, "event:" + eventKind, "The receiver did not answer in time.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Swallowed on purpose; see summary.
            }
        }

        return sent;
    }

    private async Task RecordFailureAsync(Integration integration, string trigger, string error)
    {
        try
        {
            await store.RecordDeliveryAsync(
                TenantId,
                new IntegrationDelivery(Guid.NewGuid(), integration.Id, _clock.GetUtcNow(), trigger, "POST",
                    IntegrationUrl.SafeTarget(integration.Config.Url), null, (int)DispatchTimeout.TotalMilliseconds, error),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The log is best effort.
        }
    }

    private async Task<IntegrationCallResult> DeliverAsync(Integration integration, string eventKind, object? data, CancellationToken ct)
    {
        var secrets = await store.GetSecretsAsync(TenantId, integration.Id, ct);
        var deliveryId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["source"] = "flowledger",
            ["version"] = 1,
            ["event"] = eventKind,
            ["event_id"] = deliveryId,
            ["occurred_at"] = _clock.GetUtcNow(),
            ["data"] = data,
        }, Json);

        var headers = BuildHeaders(integration.Config, secrets, new Dictionary<string, string>());
        var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        headers.Add(new(EventHeader, eventKind));
        headers.Add(new(DeliveryHeader, deliveryId));
        headers.Add(new(TimestampHeader, timestamp));
        if (secrets.SigningSecret is { } signing)
        {
            headers.Add(new(SignatureHeader, Sign(signing, timestamp, body)));
        }

        return await SendAndLogAsync(
            integration, "event:" + eventKind, new TransportRequest("POST", integration.Config.Url, headers, body, "application/json"), ct);
    }

    /// <summary>
    /// <c>sha256=hex(HMAC-SHA256(secret, timestamp + "." + body))</c>. The receiver recomputes it over the raw body and rejects
    /// timestamps that are too old, which stops a captured request from being replayed.
    /// </summary>
    public static string Sign(string secret, string timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    private static string NewSigningSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Shared plumbing ──────────────────────────────────────────────────────────

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

    private static List<KeyValuePair<string, string>> BuildHeaders(
        IntegrationConfig config, IntegrationSecrets secrets, IReadOnlyDictionary<string, string> extra)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in config.Headers)
        {
            headers.Add(new(k, v));
        }

        foreach (var (k, v) in extra)
        {
            headers.RemoveAll(h => h.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
            headers.Add(new(k, v));
        }

        if (secrets.Credential is { } credential)
        {
            switch (config.Auth)
            {
                case IntegrationAuth.Bearer:
                    headers.Add(new("Authorization", "Bearer " + credential));
                    break;
                case IntegrationAuth.Basic:
                    headers.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes((config.AuthUser ?? "") + ":" + credential))));
                    break;
                case IntegrationAuth.ApiKey:
                    headers.Add(new(config.AuthUser ?? "X-API-Key", credential));
                    break;
            }
        }

        if (headers.All(h => !h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new("User-Agent", "FlowLedger-Integrations/1"));
        }

        return headers;
    }

    private static bool TryCleanHeaders(Dictionary<string, string>? raw, out Dictionary<string, string> clean, out string? error)
    {
        clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (raw is null)
        {
            return true;
        }

        if (raw.Count > MaxHeaders)
        {
            error = $"Use at most {MaxHeaders} extra headers.";
            return false;
        }

        foreach (var (key, value) in raw)
        {
            var k = key?.Trim() ?? "";
            var v = value?.Trim() ?? "";
            if (!HeaderName().IsMatch(k) || ReservedHeaders.Contains(k))
            {
                error = $"'{k}' is not an allowed header name (authentication, framing and signature headers are managed for you).";
                return false;
            }

            if (v.Length > 1024 || v.Any(char.IsControl))
            {
                error = $"The value of '{k}' is invalid.";
                return false;
            }

            clean[k] = v;
        }

        return true;
    }

    private bool TryBuildRestUrl(string baseUrl, string? path, string? query, out string? url, out string? error)
    {
        url = null;
        error = null;
        var p = (path ?? "").Trim();
        var q = (query ?? "").Trim().TrimStart('?');
        if (p.Contains("://") || p.StartsWith("//", StringComparison.Ordinal) || p.Contains('\\') || p.Contains('#') || p.Contains(".."))
        {
            error = "Use a path under the integration's base URL (like /v1/items), not a full URL.";
            return false;
        }

        if (q.Contains('#') || q.Length > 1024)
        {
            error = "The query string is invalid.";
            return false;
        }

        if (p.Length > 0 && !p.StartsWith('/'))
        {
            p = "/" + p;
        }

        var baseUri = new Uri(baseUrl);
        var combined = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + p;
        var baseQuery = baseUri.Query.TrimStart('?');
        var fullQuery = string.Join('&', new[] { baseQuery, q }.Where(s => s.Length > 0));
        var candidate = fullQuery.Length > 0 ? combined + "?" + fullQuery : combined;
        if (!IntegrationUrl.TryValidate(candidate, AllowLocal, out url, out error))
        {
            return false;
        }

        // The path must not escape the base host (a "@" in the path can't, but be explicit).
        if (!new Uri(url!).Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            url = null;
            error = "The request must stay on the integration's host.";
            return false;
        }

        return true;
    }
}
