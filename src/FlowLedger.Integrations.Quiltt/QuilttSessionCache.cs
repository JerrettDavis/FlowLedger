using System.Collections.Concurrent;

namespace FlowLedger.Integrations.Quiltt;

/// <summary>
/// Process-wide cache of Quiltt profile sessions. A session is reused until shortly before it expires (and never longer than
/// <see cref="MaxAge"/>, which bounds how long a token that Quiltt revoked early could be served).
/// </summary>
internal sealed class QuilttSessionCache(TimeProvider? time = null, int maxEntries = QuilttSessionCache.DefaultMaxEntries)
{
    /// <summary>Upper bound on cached profiles (#179); expired entries go first, then the ones closest to expiry.</summary>
    public const int DefaultMaxEntries = 1000;

    internal static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (QuilttSession Session, DateTimeOffset UseUntil)> _items = new(StringComparer.Ordinal);

    public QuilttSession? TryGet(string profileId) =>
        _items.TryGetValue(profileId, out var e) && e.UseUntil > _time.GetUtcNow() ? e.Session : null;

    public void Put(QuilttSession session)
    {
        // Without a known expiry we cannot tell how long the token lives, so do not cache it.
        if (session.ExpiresAt is not { } expires)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var useUntil = Min(expires - ExpirySkew, now + MaxAge);
        if (useUntil > now)
        {
            _items[session.ProfileId] = (session, useUntil);
            if (_items.Count > maxEntries)
            {
                Trim(now);
            }
        }
    }

    /// <summary>Number of cached profiles (for tests and diagnostics).</summary>
    public int Count => _items.Count;

    private void Trim(DateTimeOffset now)
    {
        foreach (var kv in _items)
        {
            if (kv.Value.UseUntil <= now)
            {
                _items.TryRemove(kv.Key, out _);
            }
        }

        if (_items.Count <= maxEntries)
        {
            return;
        }

        foreach (var kv in _items.OrderBy(i => i.Value.UseUntil).Take(_items.Count - maxEntries))
        {
            _items.TryRemove(kv.Key, out _);
        }
    }

    public void Invalidate(string profileId) => _items.TryRemove(profileId, out _);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
