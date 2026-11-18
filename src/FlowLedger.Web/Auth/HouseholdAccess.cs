using System.Security.Claims;

namespace FlowLedger.Web.Auth;

/// <summary>
/// Whether the signed-in user belongs to the household (legacy) tenant, the only tenant allowed to see the global
/// "Data &amp; Backups" administration (backup state, export of ALL data). The API is authoritative: it reports
/// <c>isHousehold</c> when it resolves the login, and refuses /api/data for every other tenant regardless of the UI.
/// </summary>
public static class HouseholdAccess
{
    public const string Claim = "fl_household";

    /// <summary>Single-household mode (no OIDC) has only the configured household tenant, so it is always allowed.</summary>
    public static bool IsHousehold(ClaimsPrincipal? user, OidcOptions oidc) =>
        !oidc.Enabled || string.Equals(user?.FindFirst(Claim)?.Value, "true", StringComparison.Ordinal);
}
