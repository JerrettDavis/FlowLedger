using System.Collections.Concurrent;

namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Remembers recently used v2 tenant-assertion nonces so a captured <c>X-Tenant-Sig</c> cannot be replayed inside its validity window (#135).
/// Bounded: entries live for <see cref="Ttl"/> (twice the accepted clock skew window) and at most <see cref="MaxEntries"/> are held; when
/// full, expired entries are purged first and, if the cache is still full, the registration fails closed (a nonce only reaches the cache after
/// its HMAC verified, so only a holder of the signing key can fill it).
/// </summary>
public sealed class TenantNonceCache(TimeProvider clock, int maxEntries = TenantNonceCache.DefaultMaxEntries)
{
    public const int DefaultMaxEntries = 100_000;

    /// <summary>A header is valid for plus or minus <see cref="TenantAssertion.MaxAge"/> around its timestamp, so a nonce must be remembered for twice that.</summary>
    public static readonly TimeSpan Ttl = (TenantAssertion.MaxAge * 2) + TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly object _purgeGate = new();

    public int MaxEntries { get; } = maxEntries;

    public int Count => _seen.Count;

    /// <summary>True when the nonce was fresh (and is now consumed); false when it was already used or the cache is saturated.</summary>
    public bool TryRegister(Guid tenantId, string nonce)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var expires = now + (long)Ttl.TotalSeconds;
        var key = $"{tenantId:N}:{nonce}";

        if (_seen.Count >= MaxEntries)
        {
            Purge(now);
            if (_seen.Count >= MaxEntries)
            {
                return false;
            }
        }

        if (_seen.TryAdd(key, expires))
        {
            return true;
        }

        // Already present: a replay unless the earlier use has itself expired.
        return _seen.TryGetValue(key, out var existing) && existing <= now && _seen.TryUpdate(key, expires, existing);
    }

    private void Purge(long now)
    {
        lock (_purgeGate)
        {
            foreach (var (k, expires) in _seen)
            {
                if (expires <= now)
                {
                    _seen.TryRemove(new KeyValuePair<string, long>(k, expires));
                }
            }
        }
    }
}
