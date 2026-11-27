using FlowLedger.Application.Abstractions;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Backup state, provider usage, export, import and backup-now are global (they span every tenant), so they are
/// restricted to the household (legacy) tenant, the same tenant that owns the operator's provider credentials
/// (<see cref="FlowLedger.Infrastructure.TenancySettings"/>). Any other tenant gets 403 before any work is done.
/// With OIDC on and no Tenancy:LegacyOwner* configured nobody is mapped to that tenant, so nobody gets in (fail closed).
/// Dependencies are constructor-injected (no service locator on the request).
/// </summary>
internal sealed class HouseholdTenantOnlyFilter(IHouseholdTenantAccess access) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!access.IsHouseholdTenant)
        {
            return Results.Problem(
                title: "Data administration is limited to the household owner",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}

/// <summary>
/// Export, import and backup-now act on the WHOLE database (every tenant). See <see cref="DataAdminGate"/>: an explicit
/// <c>DataAdmin:Key</c> / <c>X-Admin-Token</c> when configured, otherwise a direct-loopback check that forwarded headers cannot defeat.
/// </summary>
internal sealed class DataAdminLoopbackFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var verdict = DataAdminGate.Evaluate(configuration, context.HttpContext);
        if (!verdict.Allowed)
        {
            return Results.Problem(title: verdict.Title, detail: verdict.Detail, statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
