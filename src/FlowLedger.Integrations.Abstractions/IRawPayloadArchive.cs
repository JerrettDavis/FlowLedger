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
/// <param name="TenantId">The tenant the payload belongs to, when known at capture time (#142). It becomes the tenant partition of the
/// storage key so one tenant's payloads can be purged or exported alone; null files the payload under <c>_unattributed</c>.</param>
public sealed record RawPayloadContext(string Method, string PathAndQuery, int StatusCode, Guid? TenantId = null);

/// <summary>
/// Opens archived payloads that were sealed at rest (#142). Plaintext objects need no decryptor; encrypted ones start with
/// <see cref="Magic"/>. Implemented by Infrastructure so provider packages (e.g. the MX replay handler) stay free of key handling.
/// </summary>
public interface IRawPayloadDecryptor
{
    /// <summary>First bytes of every encrypted archive object. Plaintext archive objects are JSON and never start with it.</summary>
    public const string Magic = "FLRAWv2:";

    /// <summary>True when <paramref name="content"/> is an encrypted archive object.</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> content) => content.StartsWith("FLRAWv2:"u8);

    /// <summary>Returns the plaintext of an archive object (plaintext objects are returned unchanged); null when it cannot be opened.</summary>
    /// <param name="provider">Provider folder name (e.g. "MX").</param>
    /// <param name="tenantSegment">The tenant folder under the provider: a 32-hex tenant id, <c>_unattributed</c>, or empty for a legacy object.</param>
    byte[]? TryDecrypt(string provider, string tenantSegment, byte[] content);
}

