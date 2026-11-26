using System.Threading.RateLimiting;
using FlowLedger.Api.Auth;
using FlowLedger.Api.Tenancy;
using Microsoft.Extensions.Options;

namespace FlowLedger.Api.Middleware;

/// <summary>
/// Rate-limit partitioning. All Web traffic reaches the API from ONE address (the Web backend), so partitioning by IP
/// alone would still make every user share one budget. Authenticated callers (valid API key) are therefore
/// partitioned by tenant, so one user (or a runaway loop in one circuit) cannot exhaust everyone else's limit;
/// unauthenticated callers (webhooks, probes, bad keys) are partitioned by remote IP. Requires
/// <c>UseRateLimiter()</c> AFTER <c>UseAuthentication()</c>, so the API key has been validated before the tenant
/// header is trusted as a partition key.
/// <para>
/// When <c>Api:TenantSigningKey</c> is set the tenant header is only used as a partition key if its signature verifies, so a
/// caller cannot mint partitions. Either way a <see cref="CreateGlobalLimiter"/> per-IP backstop applies to every request, so
/// rotating <c>X-Tenant-Id</c> values cannot exceed the IP-level cap, and failed authentication is throttled per IP.
/// </para>
/// </summary>
public static class RateLimitPartitions
{
    public const int DefaultGlobalPerIpPerMinute = 1200;
    public const int DefaultFailedAuthPerIpPerMinute = 20;
    public const int DefaultWebhookPerIpPerMinute = 300;
    private const int DefaultRetryAfterSeconds = 60;

    public static string KeyFor(HttpContext http) =>
        KeyFor(http, http.RequestServices?.GetService<IOptions<ApiOptions>>()?.Value.TenantSigningKey);

    public static string KeyFor(HttpContext http, string? signingKey)
    {
        if (http.User.Identity?.IsAuthenticated == true
            && http.User.Identity.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName)
        {
            var tenant = http.Request.Headers["X-Tenant-Id"].ToString();
            if (!Guid.TryParse(tenant, out var id))
            {
                return "tenant:none";
            }

            // Unverifiable tenant (signing key set but signature missing/invalid): the request will be rejected downstream, and
            // must not create a partition of its own.
            if (!string.IsNullOrEmpty(signingKey)
                && !TenantAssertion.Verify(signingKey, id, http.Request.Headers["X-Tenant-Sig"].ToString(), DateTimeOffset.UtcNow))
            {
                return IpKey(http);
            }

            return "tenant:" + id.ToString("N");
        }

        return IpKey(http);
    }

    private static string IpKey(HttpContext http) => "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>Webhooks are partitioned per provider and IP so junk aimed at one provider's URL cannot starve the other's.</summary>
    public static string WebhookKey(HttpContext http)
    {
        // /api/integrations/{provider}/webhooks
        var segments = (http.Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var provider = segments.Length >= 3 ? segments[2].ToLowerInvariant() : "unknown";
        return $"webhook:{provider}:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    public static void AddFixedWindowPolicy(
        this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options, string name, int permitLimit, int queueLimit) =>
        options.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(KeyFor(http), _ => Window(permitLimit, queueLimit)));

    /// <summary>Pre-verification webhook limit: generous, no queue, per (provider, IP).</summary>
    public static void AddWebhookPolicy(
        this Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options, string name, int permitLimit) =>
        options.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(WebhookKey(http), _ => Window(permitLimit, 0)));

    /// <summary>
    /// Backstop applied to every request before the named policies: a per-IP cap (health probes exempt) and a stricter per-IP
    /// cap on requests that presented API credentials which did not authenticate.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(IConfiguration config) =>
        CreateGlobalLimiter(
            config.GetValue("RateLimiting:GlobalPerIpPerMinute", DefaultGlobalPerIpPerMinute),
            config.GetValue("RateLimiting:FailedAuthPerIpPerMinute", DefaultFailedAuthPerIpPerMinute));

    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(int perIpPerMinute, int failedAuthPerIpPerMinute)
    {
        var failedAuth = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            PresentedRejectedCredentials(http)
                ? RateLimitPartition.GetFixedWindowLimiter("authfail:" + IpKey(http), _ => Window(failedAuthPerIpPerMinute, 0))
                : RateLimitPartition.GetNoLimiter("authok"));

        var perIp = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            IsProbe(http)
                ? RateLimitPartition.GetNoLimiter("probe")
                : RateLimitPartition.GetFixedWindowLimiter(IpKey(http), _ => Window(perIpPerMinute, 0)));

        return PartitionedRateLimiter.CreateChained(failedAuth, perIp);
    }

    /// <summary>Adds <c>Retry-After</c> to 429 responses.</summary>
    public static ValueTask OnRejected(Microsoft.AspNetCore.RateLimiting.OnRejectedContext context, CancellationToken ct)
    {
        TimeSpan? retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : null;
        context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds(retry).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    }

    public static int RetryAfterSeconds(TimeSpan? retryAfter) =>
        retryAfter is { } t && t > TimeSpan.Zero ? (int)Math.Ceiling(t.TotalSeconds) : DefaultRetryAfterSeconds;

    private static bool IsProbe(HttpContext http) =>
        http.Request.Path.StartsWithSegments("/health") || http.Request.Path.StartsWithSegments("/alive");

    private static bool PresentedRejectedCredentials(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            return false;
        }

        var headers = http.Request.Headers;
        return headers.ContainsKey("X-Api-Key")
            || headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    private static FixedWindowRateLimiterOptions Window(int permitLimit, int queueLimit) => new()
    {
        Window = TimeSpan.FromMinutes(1),
        PermitLimit = permitLimit,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = queueLimit,
    };
}
