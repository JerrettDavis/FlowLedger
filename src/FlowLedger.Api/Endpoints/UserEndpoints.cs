using FlowLedger.Application.Features.Users;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Identity → tenant mapping used by the web front end (a trusted backend holding the API key) after it has
/// authenticated a user through any OpenID Connect provider. Not callable without the API key.
/// </summary>
internal static class UserEndpoints
{
    internal sealed record ResolveUserRequest(string Issuer, string Subject, string? Email, string? DisplayName, bool EmailVerified = false);

    internal static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireRateLimiting("api");

        group.MapPost("/resolve", async (ResolveUserRequest req, IUserDirectory users, IConfiguration cfg, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Issuer) || string.IsNullOrWhiteSpace(req.Subject))
            {
                return Results.Problem(title: "issuer and subject are required", statusCode: StatusCodes.Status400BadRequest);
            }

            var user = await users.ResolveAsync(new ExternalIdentity(req.Issuer.Trim(), req.Subject.Trim(), req.Email, req.DisplayName, req.EmailVerified), ct);
            // isHousehold: this login maps to the household (legacy) tenant, the only one allowed to see /api/data (backups, export).
            var isHousehold = user.TenantId == FlowLedger.Infrastructure.TenancySettings.Legacy(cfg).LegacyTenantId;
            return Results.Ok(new { userId = user.UserId, tenantId = user.TenantId, email = user.Email, displayName = user.DisplayName, created = user.Created, isHousehold });
        })
        .WithName("ResolveUser")
        .WithSummary("Map an authenticated external identity to its FlowLedger user and tenant (creating on first sight)");

        // ── Linked logins: several IdP accounts, one tenant ─────────────────────────────────────────────
        group.MapGet("/identities", async (FlowLedger.SharedKernel.ITenantContext tenant, IUserDirectory users, CancellationToken ct) =>
                Results.Ok(await users.ListIdentitiesAsync(tenant.TenantId, ct)))
            .WithName("ListLinkedLogins").WithSummary("Logins linked to the caller's tenant");

        group.MapPost("/link-code", async (FlowLedger.SharedKernel.ITenantContext tenant, IUserDirectory users, CancellationToken ct) =>
            {
                var c = await users.CreateLinkCodeAsync(tenant.TenantId, ct);
                return Results.Ok(new { code = c.Code, expiresUtc = c.ExpiresUtc });
            })
            .WithName("CreateLinkCode").WithSummary("Single-use, 15-minute code that links another login to this tenant");

        group.MapPost("/link", async (RedeemLinkRequest req, FlowLedger.SharedKernel.ITenantContext tenant, IUserDirectory users, CancellationToken ct) =>
            {
                // The caller (trusted Web backend) names the signed-in login; it must belong to the tenant in the header.
                var mine = await users.ListIdentitiesAsync(tenant.TenantId, ct);
                if (mine.All(i => i.UserId != req.UserId))
                {
                    return Results.Problem(title: "login does not belong to this tenant", statusCode: StatusCodes.Status403Forbidden);
                }

                var outcome = await users.RedeemLinkCodeAsync(req.UserId, req.Code ?? string.Empty, ct);
                return outcome switch
                {
                    LinkOutcome.Linked or LinkOutcome.AlreadyLinked => Results.Ok(new { outcome = outcome.ToString() }),
                    _ => Results.Problem(title: "Invalid or expired code", statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .WithName("RedeemLinkCode").WithSummary("Link the signed-in login to the tenant that issued the code");

        group.MapDelete("/identities/{userId:guid}", async (Guid userId, FlowLedger.SharedKernel.ITenantContext tenant, IUserDirectory users, CancellationToken ct) =>
                await users.UnlinkAsync(tenant.TenantId, userId, ct)
                    ? Results.NoContent()
                    : Results.Problem(title: "Cannot unlink: unknown login or it is the only login on this tenant", statusCode: StatusCodes.Status409Conflict))
            .WithName("UnlinkLogin").WithSummary("Detach a login from this tenant (the last login cannot be detached)");

        return app;
    }

    internal sealed record RedeemLinkRequest(Guid UserId, string? Code);
}
