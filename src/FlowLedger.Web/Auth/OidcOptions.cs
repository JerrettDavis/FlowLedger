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

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
