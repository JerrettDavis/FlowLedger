namespace FlowLedger.Web.ApiClient;

/// <summary>
/// Configuration section "Api" for the Web frontend.
/// Holds the API key and household tenant id used when calling the FlowLedger API.
/// Bound from appsettings.json section "Api" (or environment variables Api__Key / Api__TenantId).
/// </summary>
public sealed class ApiAuthOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// The API key forwarded to the FlowLedger API via the X-Api-Key header.
    /// Must match Api:Key on the API service. Set via environment variable Api__Key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The household tenant guid forwarded via the X-Tenant-Id header.
    /// Defaults to the dev demo tenant when not configured.
    /// </summary>
    public string TenantId { get; set; } = "00000000-0000-0000-0000-000000000001";

    /// <summary>
    /// Optional (Api__TenantSigningKey): when set, every request also carries X-Tenant-Sig, an HMAC over the tenant id and a timestamp,
    /// which the API verifies. Must match Api:TenantSigningKey on the API. Keep it different from <see cref="Key"/>.
    /// </summary>
    public string? TenantSigningKey { get; set; }
}
