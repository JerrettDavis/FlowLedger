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
    ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TenantMappingValidator.Interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await IsStillValidAsync(
            authenticationState.User,
            (tenant, ct) => TenantMappingValidator.ListUserIdsAsync(scope.ServiceProvider, tenant, ct),
            cancellationToken);
    }

    /// <summary>The decision, separated from the circuit plumbing so it can be tested: false only on a definite mismatch.</summary>
    public static async Task<bool> IsStillValidAsync(
        ClaimsPrincipal? principal,
        Func<Guid, CancellationToken, Task<IReadOnlyCollection<Guid>?>> listUserIdsOnTenant,
        CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return true; // nothing to revalidate (anonymous, or single-household mode without sign-in)
        }

        var outcome = await TenantMappingValidator.EvaluateAsync(principal, lastChecked: null, DateTimeOffset.UtcNow, listUserIdsOnTenant, ct);
        return outcome != TenantMappingValidator.Outcome.Reject;
    }
}
