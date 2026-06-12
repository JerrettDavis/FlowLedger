using System.Net;
using System.Net.Http.Headers;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Single source of truth for translating MX HTTP failures into the provider exception taxonomy.
///
/// (PatternKit Strategy was intended here per the architecture brief, but PatternKit 0.147.2
/// does not restore on any configured NuGet feed — see Phase 4 report. Hand-rolled switch
/// expressions are used instead; correctness + green build over dogfooding.)
///
/// Mapping rules:
///   401 / 403            → FatalProviderException (bad creds / not allow-listed; no auto-retry).
///   404                  → FatalProviderException (MX often returns 404 in place of 401).
///   408 / 5xx / timeout  → TransientProviderException (retry with back-off).
///   429                  → RateLimitedProviderException (honour Retry-After when present).
///   other 4xx            → FatalProviderException.
/// CHALLENGED / needs-reauth member states are mapped separately at the call site to
/// NeedsUserActionProviderException (status codes alone cannot express them).
/// </summary>
internal static class MxErrorMapper
{
    /// <summary>Builds the appropriate <see cref="ProviderException"/> for a non-success response.</summary>
    public static ProviderException FromResponse(HttpResponseMessage response, string operation)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = (int)response.StatusCode;
        var code = response.StatusCode.ToString();

        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => new RateLimitedProviderException(
                $"MX rate limit exceeded during '{operation}'.",
                retryAfter: ResolveRetryAfter(response.Headers.RetryAfter),
                providerCode: code),

            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                new FatalProviderException(
                    $"MX rejected '{operation}' with {status} {code} (authentication or authorisation failure).",
                    providerCode: code),

            HttpStatusCode.RequestTimeout => new TransientProviderException(
                $"MX request timed out during '{operation}' ({status}).",
                retryAfter: TimeSpan.FromSeconds(5),
                providerCode: code),

            _ when status >= 500 => new TransientProviderException(
                $"MX server error during '{operation}' ({status} {code}).",
                retryAfter: TimeSpan.FromSeconds(5),
                providerCode: code),

            _ => new FatalProviderException(
                $"MX returned an unexpected {status} {code} during '{operation}'.",
                providerCode: code),
        };
    }

    /// <summary>Wraps a network-level failure (no HTTP response) as transient.</summary>
    public static TransientProviderException FromNetworkFailure(string operation, Exception inner) =>
        new($"MX request failed at the network layer during '{operation}'.",
            retryAfter: TimeSpan.FromSeconds(5),
            providerCode: "NETWORK",
            inner: inner);

    private static DateTimeOffset? ResolveRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Date is { } date)
        {
            return date;
        }

        if (retryAfter.Delta is { } delta)
        {
            return DateTimeOffset.UtcNow.Add(delta);
        }

        return null;
    }
}
