using System.Threading.RateLimiting;
using FlowLedger.Api.Auth;

namespace FlowLedger.Api.Middleware;

/// <summary>
/// Rate-limit partitioning. All Web traffic reaches the API from ONE address (the Web backend), so partitioning by IP
/// alone would still make every user share one budget. Authenticated callers (valid API key) are therefore
/// partitioned by tenant, so one user (or a runaway loop in one circuit) cannot exhaust everyone else's limit;
/// unauthenticated callers (webhooks, probes, bad keys) are partitioned by remote IP. Requires
/// <c>UseRateLimiter()</c> AFTER <c>UseAuthentication()</c>, so the API key has been validated before the tenant
/// header is trusted as a partition key.
/// </summary>
public static class RateLimitPartitions
{
    public static string KeyFor(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true
            && http.User.Identity.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName)
        {
            var tenant = http.Request.Headers["X-Tenant-Id"].ToString();
            return Guid.TryParse(tenant, out var id) ? "tenant:" + id.ToString("N") : "tenant:none";
        }

        return "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    public static void AddFixedWindowPolicy(
        this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options, string name, int permitLimit, int queueLimit) =>
        options.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(KeyFor(http), _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = permitLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = queueLimit,
        }));
}
