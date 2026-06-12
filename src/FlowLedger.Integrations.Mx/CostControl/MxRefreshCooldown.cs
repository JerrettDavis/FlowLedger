using System.Globalization;
using FlowLedger.Domain.ValueObjects;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Mx.CostControl;

/// <summary>
/// Outcome of a manual-refresh cooldown check.
/// </summary>
public readonly record struct RefreshDecision(bool Allowed, TimeSpan RetryAfter, string Reason)
{
    public static RefreshDecision Allow() => new(true, TimeSpan.Zero, "Allowed.");

    public static RefreshDecision Block(TimeSpan retryAfter) =>
        new(false, retryAfter,
            $"A manual refresh was performed recently. Try again in {Math.Ceiling(retryAfter.TotalSeconds)}s.");
}

/// <summary>
/// Per-(tenant, member) cooldown gate for manual MX refreshes, backed by the distributed cache.
///
/// MX bills per aggregation, so user-triggered refreshes are rate-limited to a configurable
/// window (<see cref="MxProviderOptions.ManualRefreshCooldown"/>). The first refresh records a
/// timestamp with a TTL equal to the cooldown window; subsequent refreshes inside the window are
/// rejected with a user-actionable <see cref="RefreshDecision"/>.
///
/// Cache backing: <see cref="IDistributedCache"/>. When Redis is wired (Aspire <c>redis</c>
/// resource) this is shared across instances; otherwise <c>AddMxProvider</c> registers the
/// in-memory distributed cache as a documented single-instance fallback.
/// </summary>
public sealed class MxRefreshCooldown
{
    private const string KeyPrefix = "mx:refresh-cooldown:";

    private readonly IDistributedCache _cache;
    private readonly MxProviderOptions _options;
    private readonly TimeProvider _clock;

    public MxRefreshCooldown(
        IDistributedCache cache,
        IOptions<MxProviderOptions> options,
        TimeProvider? clock = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Checks the cooldown for a manual refresh. If allowed, records the refresh so that
    /// subsequent calls within the window are blocked. Atomicity is best-effort (read-then-write);
    /// the underlying provider rate-limits provide the hard backstop.
    /// </summary>
    public async Task<RefreshDecision> TryBeginManualRefreshAsync(
        TenantId tenantId,
        string memberProviderId,
        CancellationToken ct = default)
    {
        var window = _options.ManualRefreshCooldown;
        if (window <= TimeSpan.Zero)
        {
            return RefreshDecision.Allow();
        }

        var key = BuildKey(tenantId, memberProviderId);
        var now = _clock.GetUtcNow();

        var existing = await _cache.GetStringAsync(key, ct).ConfigureAwait(false);
        if (existing is not null
            && long.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            var last = new DateTimeOffset(ticks, TimeSpan.Zero);
            var elapsed = now - last;
            if (elapsed < window)
            {
                return RefreshDecision.Block(window - elapsed);
            }
        }

        await _cache.SetStringAsync(
            key,
            now.UtcTicks.ToString(CultureInfo.InvariantCulture),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = window },
            ct).ConfigureAwait(false);

        return RefreshDecision.Allow();
    }

    private static string BuildKey(TenantId tenantId, string memberProviderId) =>
        $"{KeyPrefix}{tenantId.Value:N}:{memberProviderId}";
}
