using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowLedger.ServiceDefaults;

/// <summary>Who may create a new tenant by signing in through OIDC (<c>Auth:SignUp:Mode</c>).</summary>
public enum SignUpMode
{
    /// <summary>Any identity the IdP authenticates gets its own tenant (the pre-#145 behaviour; only reachable via the explicit legacy flag).</summary>
    Open,

    /// <summary>Only logins on the configured allowlist (<c>Auth:Oidc:AllowedEmails</c> / <c>AllowedEmailDomains</c>) get a new tenant. Existing logins keep working.</summary>
    InviteOnly,

    /// <summary>Nobody gets a new tenant. Existing logins and the configured household owner keep working; link codes still add logins to a tenant.</summary>
    Closed,
}

/// <summary>
/// Shared (Api, Web) reading and startup validation of the multi-user security switches (#135, #145, #159). Every check only fires when OIDC
/// sign-in is enabled and the host is not Development, so single-household and anonymous (<c>Auth:AllowAnonymous</c>) installs and CI are untouched.
/// Each failure message names its exact opt-out key so an operator can keep the old behaviour deliberately.
/// </summary>
public static partial class MultiUserSettings
{
    public const string SignUpModeKey = "Auth:SignUp:Mode";
    public const string SignUpLegacyOpenKey = "Auth:SignUp:LegacyOpen";
    public const string LegacyEmailTrustKey = "Auth:SignUp:LegacyEmailTrust";
    public const string RequireSignedTenantKey = "Api:RequireSignedTenant";
    public const string TenantSigningKeyKey = "Api:TenantSigningKey";
    public const string AllowWellKnownTenantKey = "Tenancy:AllowWellKnownLegacyTenantId";
    public const string SessionAbsoluteMaxDaysKey = "Auth:Session:AbsoluteMaxDays";
    public const string SessionLegacyCookieNameKey = "Auth:Session:LegacyCookieName";

    /// <summary>The tenant id every FlowLedger install used before #135; public knowledge, so not a secret.</summary>
    public static readonly Guid WellKnownTenantId = new("00000000-0000-0000-0000-000000000001");

    public const int MinSigningKeyLength = 32;

    /// <summary>A boolean setting; unset or blank (compose passes unset variables through as empty strings) means <paramref name="fallback"/>, never an exception.</summary>
    public static bool Flag(IConfiguration cfg, string key, bool fallback = false) =>
        bool.TryParse(cfg[key], out var value) ? value : fallback;

    /// <summary>An integer setting; unset, blank or not a number means <paramref name="fallback"/>.</summary>
    public static int Int(IConfiguration cfg, string key, int fallback) =>
        int.TryParse(cfg[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>True when OIDC sign-in is configured (mirrors <c>OidcOptions.Enabled</c>).</summary>
    public static bool OidcEnabled(IConfiguration cfg) =>
        !string.IsNullOrWhiteSpace(cfg["Auth:Oidc:Authority"]) && !string.IsNullOrWhiteSpace(cfg["Auth:Oidc:ClientId"]);

    public static bool HasAllowlist(IConfiguration cfg) =>
        Values(cfg, "Auth:Oidc:AllowedEmails").Any() || Values(cfg, "Auth:Oidc:AllowedEmailDomains").Any();

    private static IEnumerable<string> Values(IConfiguration cfg, string key)
    {
        var section = cfg.GetSection(key);
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            yield return section.Value;
        }

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                yield return child.Value;
            }
        }
    }

    /// <summary>
    /// The effective sign-up mode. An explicit <c>Auth:SignUp:Mode</c> wins; otherwise <c>Auth:SignUp:LegacyOpen=true</c> keeps the old open
    /// sign-up; otherwise the secure default <see cref="SignUpMode.InviteOnly"/> (which, with no allowlist, admits nobody new).
    /// </summary>
    public static SignUpMode ResolveSignUpMode(IConfiguration cfg)
    {
        var raw = cfg[SignUpModeKey];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (Enum.TryParse<SignUpMode>(raw.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            {
                return mode;
            }

            throw new InvalidOperationException($"{SignUpModeKey}='{raw}' is not valid. Use Open, InviteOnly or Closed.");
        }

        return Flag(cfg, SignUpLegacyOpenKey) ? SignUpMode.Open : SignUpMode.InviteOnly;
    }

    /// <summary>
    /// Whether the mode lets a login with no account yet create a tenant. For <see cref="SignUpMode.InviteOnly"/> the caller has already
    /// enforced the allowlist, so reaching here with a non-empty list means the login is invited.
    /// </summary>
    public static bool AllowsNewTenants(SignUpMode mode, bool allowlistConfigured) => mode switch
    {
        SignUpMode.Open => true,
        SignUpMode.InviteOnly => allowlistConfigured,
        _ => false,
    };

    /// <summary>
    /// Whether the API must refuse any request without a valid <c>X-Tenant-Sig</c>. Explicit <c>Api:RequireSignedTenant</c> wins; the default is
    /// true outside Development when OIDC is enabled (the shared API key is then used on behalf of many tenants).
    /// </summary>
    public static bool RequireSignedTenant(IConfiguration cfg, IHostEnvironment env) =>
        bool.TryParse(cfg[RequireSignedTenantKey], out var explicitValue)
            ? explicitValue
            : !env.IsDevelopment() && OidcEnabled(cfg);

    /// <summary>Effective household tenant id as configured (same precedence as <c>TenancySettings.Legacy</c>).</summary>
    public static Guid ConfiguredLegacyTenant(IConfiguration cfg)
    {
        foreach (var key in new[] { "Tenancy:LegacyTenantId", "Api:TenantId", "Worker:TenantId" })
        {
            if (Guid.TryParse(cfg[key], out var id) && id != Guid.Empty)
            {
                return id;
            }
        }

        return WellKnownTenantId;
    }

    /// <summary>True when some household tenant id is configured (any of the three keys holds a non-empty GUID).</summary>
    public static bool HasConfiguredLegacyTenant(IConfiguration cfg) =>
        LegacyTenantKeys.Any(key => Guid.TryParse(cfg[key], out var id) && id != Guid.Empty);

    private static readonly string[] LegacyTenantKeys = ["Tenancy:LegacyTenantId", "Api:TenantId", "Worker:TenantId"];

    /// <summary>Throws (or warns) for every unsafe multi-user combination. Call once at startup of Api and Web, after the logger exists.</summary>
    public static void Validate(IConfiguration cfg, IHostEnvironment env, ILogger logger)
    {
        // An explicit Api:RequireSignedTenant=true without a key must fail even when this host does not know OIDC is on.
        ValidateSigning(cfg, env, logger);
        if (!OidcEnabled(cfg))
        {
            return;
        }

        ValidateSignUp(cfg, env, logger);
        ValidateHouseholdTenant(cfg, env, logger);
    }

    public static void ValidateSignUp(IConfiguration cfg, IHostEnvironment env, ILogger logger)
    {
        var mode = ResolveSignUpMode(cfg);
        var allowlist = HasAllowlist(cfg);
        switch (mode)
        {
            case SignUpMode.Open:
                LogOpenSignUp(logger);
                break;
            case SignUpMode.InviteOnly when !allowlist:
                LogNobodyCanSignUp(logger);
                break;
        }

        var trustUnverified = Flag(cfg, "Tenancy:TrustUnverifiedEmail");
        if (trustUnverified)
        {
            LogTrustUnverifiedEmail(logger);
        }

        if (!env.IsDevelopment() && trustUnverified && Flag(cfg, "Tenancy:AutoLinkVerifiedEmail")
            && string.IsNullOrWhiteSpace(cfg["Tenancy:LegacyOwnerIssuer"]) && !Flag(cfg, LegacyEmailTrustKey))
        {
            throw new InvalidOperationException(
                "Tenancy:TrustUnverifiedEmail=true together with Tenancy:AutoLinkVerifiedEmail=true lets anyone who can set an arbitrary email at the " +
                "identity provider take over an existing tenant. Pin the issuer with Tenancy:LegacyOwnerIssuer, drop one of the two options, " +
                $"or accept the risk explicitly with {LegacyEmailTrustKey}=true (a self-hosted IdP you fully administer).");
        }
    }

    public static void ValidateSigning(IConfiguration cfg, IHostEnvironment env, ILogger logger)
    {
        var required = RequireSignedTenant(cfg, env);
        var key = cfg[TenantSigningKeyKey];
        if (required)
        {
            if (string.IsNullOrEmpty(key) || key.Length < MinSigningKeyLength
                || string.Equals(key, cfg["Api:Key"], StringComparison.Ordinal) || string.Equals(key, cfg["Api:PreviousKey"], StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A signed tenant assertion is required (OIDC sign-in is enabled, so the shared API key acts on behalf of many tenants, or Api:RequireSignedTenant=true). " +
                    $"Set {TenantSigningKeyKey} (env Api__TenantSigningKey, same value on api and web, at least {MinSigningKeyLength} characters, different from Api:Key; " +
                    $"`openssl rand -hex 32`). To keep the old unsigned behaviour deliberately set {RequireSignedTenantKey}=false (not recommended).");
            }
        }
        else if (OidcEnabled(cfg) && !env.IsDevelopment())
        {
            LogSigningNotRequired(logger);
        }
    }

    public static void ValidateHouseholdTenant(IConfiguration cfg, IHostEnvironment env, ILogger logger)
    {
        // Unset is decided later by the API once the database is reachable (#135): a new install generates a random id, an existing one that holds
        // data under the constant is refused there unless allowed. Only an id configured AS the constant is judged here.
        if (env.IsDevelopment() || !HasConfiguredLegacyTenant(cfg) || ConfiguredLegacyTenant(cfg) != WellKnownTenantId)
        {
            return;
        }

        if (!Flag(cfg, AllowWellKnownTenantKey))
        {
            throw new InvalidOperationException(
                $"OIDC sign-in is enabled and the household tenant id is the well-known constant {WellKnownTenantId}, which anyone holding the API key can name. " +
                "For a new install set Api__TenantId (compose: API__TENANTID) to a random GUID before the first sign-in. An existing install whose data lives under " +
                $"the well-known id must keep it and say so explicitly with {AllowWellKnownTenantKey}=true (compose: TENANCY__ALLOWWELLKNOWNLEGACYTENANTID=true); changing the id would hide the existing data.");
        }

        LogWellKnownTenant(logger);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Auth:SignUp is Open (legacy compatibility): every identity the IdP authenticates is given its own tenant. Set Auth:SignUp:Mode=InviteOnly with an allowlist, or Closed, unless the IdP itself restricts who can sign in.")]
    private static partial void LogOpenSignUp(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Auth:SignUp is InviteOnly but no allowlist (Auth:Oidc:AllowedEmails / AllowedEmailDomains) is configured: no NEW login can sign up. Existing logins and the configured household owner still sign in.")]
    private static partial void LogNobodyCanSignUp(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Tenancy:TrustUnverifiedEmail=true: an email the IdP did not verify is trusted. Safe only for a self-hosted IdP you administer; otherwise a user who can set an arbitrary email can be merged into another user's tenant.")]
    private static partial void LogTrustUnverifiedEmail(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Api:RequireSignedTenant=false with OIDC enabled: the shared API key alone can act as any tenant. Set Api:TenantSigningKey and remove the override.")]
    private static partial void LogSigningNotRequired(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Tenancy:AllowWellKnownLegacyTenantId=true: the household tenant uses the publicly known id. Anyone holding the API key can act as the household owner; consider migrating to a random tenant id.")]
    private static partial void LogWellKnownTenant(ILogger logger);
}
