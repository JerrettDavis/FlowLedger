using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace FlowLedger.Web.Auth;

/// <summary>
/// Re-checks the login to tenant mapping inside an already-open Blazor circuit (#159). The cookie validator only runs on HTTP requests, and an
/// interactive circuit can stay open for days without one, so an unlinked or moved login would keep the old tenant's data on screen.
/// Every <see cref="RevalidationInterval"/> this asks the same question and applies the same rule as <see cref="TenantMappingValidator"/>:
/// a definite mismatch ends the session, an API that cannot answer keeps it (an outage never signs everyone out). No new policy is introduced.
/// </summary>
public sealed class TenantRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory, OidcOptions oidc) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TenantMappingValidator.Interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await IsStillValidAsync(
            authenticationState.User,
            (tenant, ct) => TenantMappingValidator.ListUserIdsAsync(scope.ServiceProvider, tenant, ct),
            cancellationToken,
            oidc.SessionAbsoluteMaxDays);
    }

    /// <summary>The decision, separated from the circuit plumbing so it can be tested: false only on a definite mismatch.</summary>
    public static async Task<bool> IsStillValidAsync(
        ClaimsPrincipal? principal,
        Func<Guid, CancellationToken, Task<IReadOnlyCollection<Guid>?>> listUserIdsOnTenant,
        CancellationToken ct = default,
        int absoluteMaxDays = 0,
        DateTimeOffset? now = null)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return true; // nothing to revalidate (anonymous, or single-household mode without sign-in)
        }

        // #159: an open circuit never runs the cookie validator, so the absolute session cap is enforced here too. An unstamped (pre-cap)
        // principal is left to the next HTTP request, which stamps it.
        if (AuthSetup.IsPastAbsoluteLifetime(principal, now ?? DateTimeOffset.UtcNow, absoluteMaxDays) == true)
        {
            return false;
        }

        var outcome = await TenantMappingValidator.EvaluateAsync(principal, lastChecked: null, DateTimeOffset.UtcNow, listUserIdsOnTenant, ct);
        return outcome != TenantMappingValidator.Outcome.Reject;
    }
}
