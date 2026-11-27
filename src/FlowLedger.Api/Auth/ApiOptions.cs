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

    /// <summary>
    /// Optional second secret (Api:TenantSigningKey), distinct from <see cref="Key"/>. When set, every HTTP request must carry a valid
    /// <c>X-Tenant-Sig</c> from the Web app proving it vouches for the <c>X-Tenant-Id</c>; a leaked API key alone then cannot act as
    /// an arbitrary tenant. Must match Api:TenantSigningKey on the Web app.
    /// </summary>
    public string? TenantSigningKey { get; set; }

    /// <summary>
    /// Effective value of <c>Api:RequireSignedTenant</c> (#135): when true every HTTP request must carry a valid <c>X-Tenant-Sig</c> and a missing
    /// <see cref="TenantSigningKey"/> fails startup. Unset, it defaults to true outside Development when OIDC sign-in is configured on this
    /// service (<c>Auth:Oidc:Authority</c>/<c>ClientId</c>), because the shared API key then acts for many tenants. Set false to keep the legacy
    /// unsigned behaviour deliberately. Filled in by a PostConfigure in Program.cs (it needs the environment).
    /// </summary>
    public bool RequireSignedTenant { get; set; }

    /// <summary>
    /// Optional rotation key (Api:PreviousKey). While set, callers presenting it are still authenticated (and a Warning is logged),
    /// so Api, Web and Worker can be restarted with the new <see cref="Key"/> one at a time. Remove it once rotation is complete.
    /// </summary>
    public string? PreviousKey { get; set; }

    /// <summary>The effective household tenant id for background jobs.</summary>
    public Guid EffectiveTenantId =>
        TenantId is { } id && id != Guid.Empty ? id : DefaultTenantId;
}
