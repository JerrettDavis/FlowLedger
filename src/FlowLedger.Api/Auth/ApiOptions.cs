using System.ComponentModel.DataAnnotations;

namespace FlowLedger.Api.Auth;

/// <summary>
/// Configuration section "Api" — controls the API key for the self-hosted API.
/// In Production, Key must be a non-empty, non-default value set via environment
/// variable (Api__Key), user secrets, or appsettings.Production.local.json.
/// The startup guard in Program.cs enforces the non-empty/non-default check at boot.
/// </summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Default single-household tenant id used by background jobs when
    /// <see cref="TenantId"/> is not explicitly configured. Matches the demo tenant.</summary>
    public static readonly Guid DefaultTenantId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// The API key callers must supply as "Authorization: Bearer {Key}" or
    /// "X-Api-Key: {Key}". Empty in committed appsettings.json — set externally
    /// (environment variable Api__Key or user secrets) in all environments.
    /// <c>[MinLength(1)]</c> ensures an empty key fails ValidateOnStart (in addition to
    /// <c>[Required]</c>, which only rejects null).
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "Api:Key must be a non-empty value.")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The single household tenant id used by background jobs (e.g. webhook-triggered
    /// sync) that run with no HttpContext. Production operators should set
    /// <c>Api:TenantId</c> to their household tenant; when empty it falls back to
    /// <see cref="DefaultTenantId"/>. This is NOT used for HTTP requests — those resolve
    /// the tenant from the X-Tenant-Id header and fail closed when it is absent.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>The effective household tenant id for background jobs.</summary>
    public Guid EffectiveTenantId =>
        TenantId is { } id && id != Guid.Empty ? id : DefaultTenantId;
}
