using System.Net;
using System.Net.Http.Headers;
using FlowLedger.Integrations.Abstractions;
using PatternKit.Behavioral.Strategy;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Single source of truth for translating MX HTTP failures into the provider exception taxonomy.
///
/// Uses PatternKit.Core's <see cref="Strategy{TIn,TOut}"/> (first-match semantics) so each
/// HTTP-status → exception rule is declared once, in isolation, and is independently testable.
/// The strategy is built once at class-load time (immutable, compiled artifact).
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
    // Context tuple used as input to the Strategy. Bundling response + operation avoids
    // a separate closure capture per call, keeping the strategy a pure value-mapping artifact.
    private readonly record struct MxErrorContext(HttpResponseMessage Response, string Operation);

    // Built once at class-load time; Strategy is an immutable, compiled artifact.
    private static readonly Strategy<MxErrorContext, ProviderException> _strategy =
        Strategy<MxErrorContext, ProviderException>.Create()

            // 429 — rate limited; honour Retry-After when present.
            .When(static (in MxErrorContext ctx) => ctx.Response.StatusCode == HttpStatusCode.TooManyRequests)
            .Then(static (in MxErrorContext ctx) => (ProviderException)new RateLimitedProviderException(
                $"MX rate limit exceeded during '{ctx.Operation}'.",
                retryAfter: ResolveRetryAfter(ctx.Response.Headers.RetryAfter),
                providerCode: ctx.Response.StatusCode.ToString()))

            // 401 / 403 / 404 → fatal (auth failure or MX 404-for-401 pattern).
            .When(static (in MxErrorContext ctx) => ctx.Response.StatusCode is
                HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound)
            .Then(static (in MxErrorContext ctx) => (ProviderException)new FatalProviderException(
                $"MX rejected '{ctx.Operation}' with {(int)ctx.Response.StatusCode} " +
                $"{ctx.Response.StatusCode} (authentication or authorisation failure).",
                providerCode: ctx.Response.StatusCode.ToString()))

            // 408 → transient (server-side timeout).
            .When(static (in MxErrorContext ctx) => ctx.Response.StatusCode == HttpStatusCode.RequestTimeout)
            .Then(static (in MxErrorContext ctx) => (ProviderException)new TransientProviderException(
                $"MX request timed out during '{ctx.Operation}' ({(int)ctx.Response.StatusCode}).",
                retryAfter: TimeSpan.FromSeconds(5),
                providerCode: ctx.Response.StatusCode.ToString()))

            // 5xx → transient (server error, retry with back-off).
            .When(static (in MxErrorContext ctx) => (int)ctx.Response.StatusCode >= 500)
            .Then(static (in MxErrorContext ctx) => (ProviderException)new TransientProviderException(
                $"MX server error during '{ctx.Operation}' ({(int)ctx.Response.StatusCode} {ctx.Response.StatusCode}).",
                retryAfter: TimeSpan.FromSeconds(5),
                providerCode: ctx.Response.StatusCode.ToString()))

            // Anything else (unexpected 4xx) → fatal.
            .Default(static (in MxErrorContext ctx) => (ProviderException)new FatalProviderException(
                $"MX returned an unexpected {(int)ctx.Response.StatusCode} " +
                $"{ctx.Response.StatusCode} during '{ctx.Operation}'.",
                providerCode: ctx.Response.StatusCode.ToString()))

            .Build();

    /// <summary>Builds the appropriate <see cref="ProviderException"/> for a non-success response.</summary>
    public static ProviderException FromResponse(HttpResponseMessage response, string operation)
    {
        ArgumentNullException.ThrowIfNull(response);
        var ctx = new MxErrorContext(response, operation);
        return _strategy.Execute(in ctx);
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
