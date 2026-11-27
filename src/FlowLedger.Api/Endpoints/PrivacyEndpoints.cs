using FlowLedger.Application.Features.Users;
using FlowLedger.SharedKernel;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Owner self-service privacy actions (#189). Unlike <c>/api/data/*</c> these are NOT household-admin routes: they act on the caller's own
/// tenant only (the id comes from the verified tenant context, never from the request).
/// </summary>
internal static class PrivacyEndpoints
{
    internal sealed record DeleteMyDataRequest(string? Confirmation);

    internal static IEndpointRouteBuilder MapPrivacyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").WithTags("Privacy").RequireRateLimiting("write");

        group.MapPost("/delete-my-data", async (DeleteMyDataRequest? body, ITenantContext tenant, DeleteMyDataHandler handler, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var outcome = await handler.HandleAsync(tenant.TenantId, body?.Confirmation, ct);
            var audit = loggers.CreateLogger("TenantSelfErasure");
            if (outcome.ConfirmationMismatch)
            {
                return Results.Problem(title: outcome.Refusal, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!outcome.Done)
            {
                ApiLog.TenantSelfErasureRefused(audit, tenant.TenantId);
                return Results.Problem(title: "Deletion refused", detail: outcome.Refusal, statusCode: StatusCodes.Status409Conflict);
            }

            var erasure = outcome.Erasure!;
            // Audit line: ids and counts only (Guids and integers cannot carry log-forging text).
            ApiLog.TenantSelfErased(audit, tenant.TenantId, erasure.TotalRows, erasure.RowsDeleted.Count, erasure.FilesDeleted);
            return Results.Ok(new
            {
                tenantId = tenant.TenantId,
                rowsDeleted = erasure.RowsDeleted,
                totalRows = erasure.TotalRows,
                filesDeleted = erasure.FilesDeleted,
                erasedUtc = erasure.ErasedUtc,
                providers = outcome.Providers.Select(p => new { connector = p.Connector, status = p.Status.ToString().ToLowerInvariant() }),
            });
        })
        .WithName("DeleteMyData")
        .WithSummary("Permanently erase every row and stored file of the caller's own tenant after typed confirmation; asks bank connectors to revoke first (best effort); audited");

        return app;
    }
}
