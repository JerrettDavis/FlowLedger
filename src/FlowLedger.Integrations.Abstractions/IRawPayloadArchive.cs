namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// Durable, append-only archive of the exact bytes received from an external provider.
///
/// Real provider data is billable and cannot be re-fetched for free, so every response is
/// persisted BEFORE it is parsed or mapped. If a mapping bug is found later, the archive lets
/// us re-ingest without paying the provider again.
///
/// Contract: if <see cref="ArchiveAsync"/> throws, the caller MUST fail the operation —
/// silently continuing would mean data was consumed without being retained.
/// </summary>
public interface IRawPayloadArchive
{
    /// <summary>Persists <paramref name="payload"/> and returns the storage key it was written under.</summary>
    /// <param name="provider">Provider name, e.g. "MX".</param>
    /// <param name="operation">Operation/endpoint label, e.g. "GetTransactions", "Webhook".</param>
    Task<string> ArchiveAsync(
        string provider,
        string operation,
        ReadOnlyMemory<byte> payload,
        RawPayloadContext? context = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request metadata stored in a <c>.meta.json</c> sidecar next to the payload, so an archived page can
/// always be traced to the account/page/window that produced it (an empty page is otherwise anonymous).
/// </summary>
/// <param name="Method">HTTP method.</param>
/// <param name="PathAndQuery">Request path and query string (contains provider ids but no credentials).</param>
/// <param name="StatusCode">HTTP status of the response.</param>
public sealed record RawPayloadContext(string Method, string PathAndQuery, int StatusCode);
