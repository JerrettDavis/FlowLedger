namespace FlowLedger.Web.Auth;

/// <summary>
/// Provider-agnostic OpenID Connect settings (section "Auth:Oidc"). Works with any compliant IdP: authentik,
/// Keycloak, Entra ID, Google, Zitadel, etc. via its issuer URL. Leave <see cref="Authority"/> empty to run in
/// single-household mode without sign-in (development only).
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Auth:Oidc";

    /// <summary>Issuer URL, e.g. https://auth.example.com/application/o/flowledger/ (authentik) or https://idp/realms/x (Keycloak).</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Extra scopes beyond openid/profile/email.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>Set false only for plain-http IdPs on a trusted LAN.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Claim holding the display name (falls back to name, then preferred_username).</summary>
    public string NameClaim { get; set; } = "name";

    /// <summary>
    /// Optional sign-up gate. When either list is non-empty, only logins with a verified email that matches an entry in
    /// <see cref="AllowedEmails"/> or whose domain is in <see cref="AllowedEmailDomains"/> are provisioned. Both empty = anyone
    /// the IdP authenticates is allowed (restrict at the IdP instead). Set this when the IdP is open to the public.
    /// </summary>
    public string[] AllowedEmails { get; set; } = [];

    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>
    /// Effective <c>Auth:SignUp:Mode</c> (#145), filled in by <see cref="AuthSetup.AddFlowLedgerAuth"/>: whether a login with no account yet may create a
    /// tenant. The secure default is <see cref="FlowLedger.ServiceDefaults.SignUpMode.InviteOnly"/>; <c>Auth:SignUp:LegacyOpen=true</c> restores open sign-up.
    /// </summary>
    public FlowLedger.ServiceDefaults.SignUpMode SignUpMode { get; set; } = FlowLedger.ServiceDefaults.SignUpMode.InviteOnly;

    /// <summary>True when a login the allowlist already admitted (or that no allowlist applies to) may be given a brand-new tenant.</summary>
    public bool AllowsNewTenants => FlowLedger.ServiceDefaults.MultiUserSettings.AllowsNewTenants(SignUpMode, HasAllowlist);

    /// <summary>Effective <c>Auth:Session:AbsoluteMaxDays</c> (#159): a session ends this many days after sign-in however active it is. 0 or less disables the cap.</summary>
    public int SessionAbsoluteMaxDays { get; set; } = AuthSetup.DefaultSessionAbsoluteMaxDays;

    /// <summary>Effective <c>Auth:Session:LegacyCookieName</c> (#159): keep the pre-<c>__Host-</c> cookie name <c>flowledger.auth</c> outside Development.</summary>
    public bool LegacyCookieName { get; set; }

    // Blank entries (an unset compose passthrough such as Auth__Oidc__AllowedEmails__0=) must not count as an allowlist.
    public bool HasAllowlist => AllowedEmails.Any(e => !string.IsNullOrWhiteSpace(e)) || AllowedEmailDomains.Any(d => !string.IsNullOrWhiteSpace(d));

    /// <summary>True when no allowlist is configured, or the verified email satisfies it.</summary>
    public bool IsEmailAllowed(string? email, bool emailVerified)
    {
        if (!HasAllowlist)
        {
            return true;
        }

        if (!emailVerified || string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1)
        {
            return false;
        }

        var domain = email[(at + 1)..];
        return AllowedEmails.Any(e => string.Equals(e?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            || AllowedEmailDomains.Any(d => string.Equals(d?.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
