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
/// caller cannot mint partitions; without it the header is folded into <see cref="UnverifiedTenantBuckets"/> fixed buckets. Either way a <see cref="CreateGlobalLimiter"/> per-IP backstop applies to every request, so
/// rotating <c>X-Tenant-Id</c> values cannot exceed the IP-level cap, and failed authentication is throttled per IP.
/// </para>
/// </summary>
public static class RateLimitPartitions
{
    public const int DefaultGlobalPerIpPerMinute = 1200;
    public const int DefaultFailedAuthPerIpPerMinute = 20;
    /// <summary>
    /// Blanket pre-verification webhook cap per (provider, IP). Deliberately as generous as the global per-IP cap: the signature-aware
    /// <c>WebhookAbuseLimiter</c> does the real throttling after verification, so junk cannot use up what real deliveries need (#152).
    /// </summary>
    public const int DefaultWebhookPerIpPerMinute = 1200;
    private const int DefaultRetryAfterSeconds = 60;

    /// <summary>Partition count for tenant headers that were NOT cryptographically verified; bounds limiter memory when a caller rotates <c>X-Tenant-Id</c>.</summary>
    public const int UnverifiedTenantBuckets = 1024;

    /// <summary>Default cap on distinct verified-tenant partitions held at once (<c>RateLimiting:MaxTenantPartitions</c>).</summary>
    public const int DefaultMaxTenantPartitions = 10_000;

    /// <summary>Partition count for verified tenants beyond the cap; they are folded into this many shared overflow buckets.</summary>
    public const int OverflowBuckets = 256;

    private static readonly TenantPartitionRegistry Registry = new();

    public static string KeyFor(HttpContext http)
    {
        var services = http.RequestServices;
        var max = services?.GetService<IConfiguration>()?.GetValue("RateLimiting:MaxTenantPartitions", DefaultMaxTenantPartitions)
                  ?? DefaultMaxTenantPartitions;
        return KeyFor(http, services?.GetService<IOptions<ApiOptions>>()?.Value.TenantSigningKey, max, Registry, services?.GetService<TimeProvider>());
    }

    public static string KeyFor(HttpContext http, string? signingKey, TimeProvider? clock = null) =>
        KeyFor(http, signingKey, DefaultMaxTenantPartitions, Registry, clock);

    public static string KeyFor(HttpContext http, string? signingKey, int maxTenantPartitions) =>
        KeyFor(http, signingKey, maxTenantPartitions, Registry, null);

    /// <summary>Exposed for tests so the cap can be exercised without sharing process-wide state.</summary>
    internal static string KeyFor(HttpContext http, string? signingKey, int maxTenantPartitions, TenantPartitionRegistry registry, TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
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
                && !TenantAssertion.Check(signingKey, id, http.Request.Headers["X-Tenant-Sig"].ToString(), now, http.Request.Method,
                    http.Request.PathBase.Add(http.Request.Path).Value ?? string.Empty, acceptLegacy: true, nonces: null))
            {
                return IpKey(http);
            }

            // With a signing key the id is proven, so each tenant gets its own partition. Without one the header is only as
            // trustworthy as the caller, so fold it into a fixed number of buckets: rotating ids cannot grow the limiter's state.
            if (string.IsNullOrEmpty(signingKey))
            {
                return "tenant:b" + (BitConverter.ToUInt32(id.ToByteArray(), 0) % UnverifiedTenantBuckets);
            }

            // A verified id is minted only by the holder of the signing key, but cap the number of simultaneously live partitions anyway
            // (a leaked key, or a very large deployment, must not grow limiter state without bound): beyond the cap new tenants share
            // a fixed set of overflow buckets until idle partitions age out.
            var key = "tenant:" + id.ToString("N");
            return registry.TryAdmit(key, maxTenantPartitions, now)
                ? key
                : "tenant:o" + (BitConverter.ToUInt32(id.ToByteArray(), 0) % OverflowBuckets);
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

/// <summary>
/// Tracks which verified-tenant partition keys are live so the limiter's partition count stays bounded. A key is "live" for
/// <see cref="IdleAfter"/> after its last request (a fixed-window limiter idles out about that fast); when the registry is full it
/// first drops idle keys and only then refuses new ones. Process-local by design (the limiter itself is per process).
/// </summary>
internal sealed class TenantPartitionRegistry
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    public int Count
    {
        get { lock (_gate) { return _lastSeen.Count; } }
    }

    public bool TryAdmit(string key, int max, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastSeen.ContainsKey(key))
            {
                _lastSeen[key] = now;
                return true;
            }

            if (_lastSeen.Count >= max)
            {
                var stale = _lastSeen.Where(kv => now - kv.Value > IdleAfter).Select(kv => kv.Key).ToList();
                foreach (var k in stale)
                {
                    _lastSeen.Remove(k);
                }

                if (_lastSeen.Count >= max)
                {
                    return false;
                }
            }

            _lastSeen[key] = now;
            return true;
        }
    }
}
