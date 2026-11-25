namespace FlowLedger.Application.Features.Integrations;

/// <summary>Per-tenant integration definitions, their encrypted secrets and the recent delivery log.</summary>
public interface IIntegrationStore
{
    /// <summary>False when no encryption key is configured: credentials and signing secrets are never stored in clear text.</summary>
    bool CanStoreSecrets { get; }

    Task<IReadOnlyList<Integration>> ListAsync(Guid tenantId, CancellationToken ct = default);

    Task<Integration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>The decrypted secrets, for sending only. Never expose through the API.</summary>
    Task<IntegrationSecrets> GetSecretsAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Inserts or updates. Null <paramref name="credential"/>/<paramref name="signingSecret"/> keep the stored value.</summary>
    Task SaveAsync(Guid tenantId, Integration integration, string? credential, bool clearCredential, string? signingSecret, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Appends a delivery to the log, updates last-used/last-error and trims the log.</summary>
    Task RecordDeliveryAsync(Guid tenantId, IntegrationDelivery delivery, CancellationToken ct = default);

    Task<IReadOnlyList<IntegrationDelivery>> ListDeliveriesAsync(Guid tenantId, Guid id, int limit, CancellationToken ct = default);
}

public sealed record TransportRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    string? ContentType);

/// <summary>The response. <see cref="Error"/> is a short, safe summary (never the URL or credentials); no exception escapes.</summary>
public sealed record TransportResponse(int? StatusCode, string? ContentType, string Body, bool Truncated, string? Error);

/// <summary>Performs one outbound HTTP request through the SSRF-guarded client (no redirects, short timeout, capped response).</summary>
public interface IIntegrationTransport
{
    Task<TransportResponse> SendAsync(TransportRequest request, bool allowLocalNetwork, CancellationToken ct = default);
}
