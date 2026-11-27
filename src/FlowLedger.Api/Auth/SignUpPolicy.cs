using FlowLedger.ServiceDefaults;

namespace FlowLedger.Api.Auth;

/// <summary>
/// The API's own view of the sign-up policy (#145), read from the same <c>Auth:SignUp:*</c> / <c>Auth:Oidc:*</c> keys the Web app uses.
/// The Web app enforces the allowlist (it sees the verified email); the API enforces what it can on its own so that a caller who merely
/// holds the API key cannot mint tenants through <c>/api/users/resolve</c> while sign-up is closed, and gates billable provisioning (Quiltt).
/// When the API is not told that OIDC is on (<c>Auth:Oidc:Authority</c>/<c>ClientId</c> not set on the api service) it enforces nothing here.
/// </summary>
public sealed record SignUpPolicy(bool OidcEnabled, SignUpMode Mode, bool AllowlistConfigured, bool AllowOpenSignupBillable = false)
{
    public const string AllowOpenSignupBillableKey = "Quiltt:AllowOpenSignupTenants";

    public static SignUpPolicy From(IConfiguration cfg) =>
        new(MultiUserSettings.OidcEnabled(cfg), MultiUserSettings.ResolveSignUpMode(cfg), MultiUserSettings.HasAllowlist(cfg),
            cfg.GetValue(AllowOpenSignupBillableKey, false));

    /// <summary>True when the API itself may let an unknown login create a tenant (the Web app's allowlist check still applies on top).</summary>
    public bool AllowsNewTenants => !OidcEnabled || MultiUserSettings.AllowsNewTenants(Mode, AllowlistConfigured);

    /// <summary>
    /// True when a non-household tenant may NOT provision billable provider resources (Quiltt profiles): sign-up is Open, so a tenant may belong to
    /// a stranger the operator never invited, and <c>Quiltt:AllowOpenSignupTenants</c> was not set. InviteOnly/Closed tenants are all invited.
    /// </summary>
    public bool BlocksUninvitedBillableProvisioning => OidcEnabled && Mode == SignUpMode.Open && !AllowOpenSignupBillable;
}
