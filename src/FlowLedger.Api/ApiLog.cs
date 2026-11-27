namespace FlowLedger.Api;

/// <summary>Source-generated log messages for the API host (CA1848). Parameters carry ids/counts or pre-sanitised text only.</summary>
internal static partial class ApiLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "API key authentication failed for {Method} {Path} from {RemoteIp}")]
    internal static partial void ApiKeyAuthFailed(ILogger logger, string? method, string? path, System.Net.IPAddress? remoteIp);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API request authenticated with Api:PreviousKey (rotation in progress) from {RemoteIp}; update the caller to the current key and remove Api:PreviousKey.")]
    internal static partial void PreviousKeyUsed(ILogger logger, System.Net.IPAddress? remoteIp);

    // Audit line: ids and counts only (Guids and integers cannot carry log-forging text).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId} offboarded: {Rows} rows across {Tables} tables, {Files} files erased")]
    internal static partial void TenantOffboarded(ILogger logger, Guid tenantId, int rows, int tables, int files);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId} offboarding refused")]
    internal static partial void TenantOffboardingRefused(ILogger logger, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId} erased its own data (self-service): {Rows} rows across {Tables} tables, {Files} files erased")]
    internal static partial void TenantSelfErased(ILogger logger, Guid tenantId, int rows, int tables, int files);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {TenantId} self-service deletion refused")]
    internal static partial void TenantSelfErasureRefused(ILogger logger, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secrets re-encrypted: {Resealed} resealed, {Current} already current, {Unreadable} unreadable")]
    internal static partial void SecretsReencrypted(ILogger logger, int resealed, int current, int unreadable);

    // Counts only.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Raw archive sealed: {Sealed} sealed, {Already} already sealed, {Skipped} skipped, {Recovered} recovered")]
    internal static partial void RawArchiveSealed(ILogger logger, int @sealed, int already, int skipped, int recovered);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook replay store unavailable; relying on the in-process guard for this delivery.")]
    internal static partial void ReplayStoreUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit event {Action} could not be recorded.")]
    internal static partial void AuditNotRecorded(ILogger logger, Exception exception, string action);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY: running in Development with the public dev API key, and requests without X-Tenant-Id fall back to the household tenant. Do not expose this instance or onboard other users until ASPNETCORE_ENVIRONMENT=Production (or at least Tenancy__RequireTenantHeader=true) and a real Api__Key are set.")]
    internal static partial void DevKeyInUse(ILogger logger);
}
