using FlowLedger.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace FlowLedger.Api.Auth;

/// <summary>
/// The "household operator" rule as a registered authorization policy (#94): the household tenant owns the instance-wide provider
/// credentials, paid feeds and whole-database administration, so those endpoints say <c>.RequireAuthorization(HouseholdPolicy.Name)</c>
/// instead of checking a tenant id inline. Any other tenant gets 403 before any work is done; with OIDC on and no
/// <c>Tenancy:LegacyOwner*</c> configured nobody maps to that tenant, so nobody gets in (fail closed).
/// </summary>
internal static class HouseholdPolicy
{
    public const string Name = "Household";

    /// <summary>The policy: an authenticated caller whose current tenant is the household tenant.</summary>
    internal static AuthorizationPolicy Build() =>
        new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .AddRequirements(new HouseholdOperatorRequirement())
            .Build();
}

/// <summary>Marks a policy that only the household tenant satisfies.</summary>
internal sealed class HouseholdOperatorRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Succeeds when <see cref="IHouseholdTenantAccess"/> says the current tenant is the household tenant. The access service is resolved only
/// when the requirement is evaluated: the framework constructs every registered handler for every request, and resolving the tenant
/// eagerly would make anonymous probes (/health) fail with a tenant-resolution error.
/// </summary>
internal sealed class HouseholdOperatorHandler(IServiceProvider services) : AuthorizationHandler<HouseholdOperatorRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, HouseholdOperatorRequirement requirement)
    {
        if (services.GetRequiredService<IHouseholdTenantAccess>().IsHouseholdTenant)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Turns a failed household requirement into the same ProblemDetails 403 the API returns elsewhere; every other outcome is the framework default.</summary>
internal sealed class HouseholdAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<HouseholdOperatorRequirement>().Any() == true)
        {
            await Results.Problem(
                title: "This operation is limited to the household owner",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
