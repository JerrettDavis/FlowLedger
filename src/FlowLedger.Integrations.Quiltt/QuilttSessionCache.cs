using System.Collections.Concurrent;

namespace FlowLedger.Integrations.Quiltt;

/// <summary>
/// Process-wide cache of Quiltt profile sessions. A session is reused until shortly before it expires (and never longer than
/// <see cref="MaxAge"/>, which bounds how long a token that Quiltt revoked early could be served).
/// </summary>
public sealed class QuilttSessionCache(TimeProvider? time = null)
{
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
        }
    }

    public void Invalidate(string profileId) => _items.TryRemove(profileId, out _);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
