namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// One call to a provider API, for cost accounting across the whole host (#154). Host-wide on purpose: no tenant column, no tenant
/// filter. Reached only through <see cref="HostDb"/> (an explicitly unscoped context) and listed in the tenant guard tests as global.
/// </summary>
internal sealed class ProviderApiCallRecord
{
    public long Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Method { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public int? StatusCode { get; private set; }
    public long DurationMs { get; private set; }
    public long? ResponseBytes { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public string? Error { get; private set; }

    private ProviderApiCallRecord() { }

    public static ProviderApiCallRecord Create(
        string provider, string method, string path, int? statusCode, long durationMs, long? responseBytes, DateTimeOffset occurredUtc, string? error) =>
        new()
        {
            Provider = provider,
            Method = method,
            Path = path,
            StatusCode = statusCode,
            DurationMs = durationMs,
            ResponseBytes = responseBytes,
            OccurredUtc = occurredUtc,
            Error = error,
        };
}
