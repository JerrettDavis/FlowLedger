using System.Threading.RateLimiting;

namespace FlowLedger.Api.Endpoints;

internal static class WebhookAbuse
{
    /// <summary>429 with a Retry-After of one window.</summary>
    public static IResult TooManyRequests(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "60";
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}

/// <summary>
/// Signature-aware webhook limiting (#152). The blanket pre-verification limit counts every request alike, so junk from the address a
/// provider also delivers from could use up the budget real deliveries need. Here the two outcomes are budgeted separately, AFTER the
/// signature check: a request that fails verification spends from a small per-(provider, IP) budget (beyond it the caller gets 429 instead
/// of 401), while a verified delivery is only ever limited by a generous per-provider budget. A flood of unsigned requests therefore
/// cannot stop a signed delivery. Process-local (limiter partitions are evicted when idle).
/// </summary>
public sealed class WebhookAbuseLimiter : IDisposable
{
    public const int DefaultInvalidPerIpPerMinute = 30;
    public const int DefaultVerifiedPerProviderPerMinute = 600;

    private readonly PartitionedRateLimiter<string> _invalid;
    private readonly PartitionedRateLimiter<string> _verified;

    public WebhookAbuseLimiter(int invalidPerIpPerMinute = DefaultInvalidPerIpPerMinute, int verifiedPerProviderPerMinute = DefaultVerifiedPerProviderPerMinute)
    {
        _invalid = Create(invalidPerIpPerMinute);
        _verified = Create(verifiedPerProviderPerMinute);
    }

    /// <summary>Spends one failed-verification token for (provider, ip); false once that source has failed too often this minute.</summary>
    public bool AllowInvalidResponse(string provider, string? ip)
    {
        using var lease = _invalid.AttemptAcquire($"{provider}|{ip ?? "unknown"}");
        return lease.IsAcquired;
    }

    /// <summary>Admits one verified delivery for <paramref name="provider"/>; false when the provider exceeds its verified budget.</summary>
    public bool AllowVerifiedDelivery(string provider)
    {
        using var lease = _verified.AttemptAcquire(provider);
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        _invalid.Dispose();
        _verified.Dispose();
    }

    private static PartitionedRateLimiter<string> Create(int permitsPerMinute) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = permitsPerMinute,
            QueueLimit = 0,
        }));
}
