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

        group.MapPost("/resolve", async (ResolveUserRequest req, IUserDirectory users, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Issuer) || string.IsNullOrWhiteSpace(req.Subject))
            {
                return Results.Problem(title: "issuer and subject are required", statusCode: StatusCodes.Status400BadRequest);
            }

            var user = await users.ResolveAsync(new ExternalIdentity(req.Issuer.Trim(), req.Subject.Trim(), req.Email, req.DisplayName, req.EmailVerified), ct);
            return Results.Ok(new { userId = user.UserId, tenantId = user.TenantId, email = user.Email, displayName = user.DisplayName, created = user.Created });
        })
        .WithName("ResolveUser")
        .WithSummary("Map an authenticated external identity to its FlowLedger user and tenant (creating on first sight)");

        return app;
    }
}
