namespace FlowLedger.Api.Endpoints;

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
