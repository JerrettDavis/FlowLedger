namespace FlowLedger.Application.Abstractions;

/// <summary>
/// A per-tenant "data version": a stamp that changes whenever the tenant's transactions, accounts, categories or balance snapshots are
/// saved. Cached read results are keyed by it, so a write invalidates them (#160). Process-local: with several API replicas another
/// replica sees the change only when its short cache TTL lapses.
/// </summary>
public interface ITenantDataVersion
{
    long Get(Guid tenantId);

    void Bump(Guid tenantId);
}

/// <summary>
/// In-memory <see cref="ITenantDataVersion"/>. Stamps come from one global counter, so a stamp is never reused: if a tenant's entry is
/// evicted (the map is bounded), the next read mints a fresh stamp and every result cached under the old one simply misses.
/// </summary>
public sealed class TenantDataVersion(int maxTenants = TenantDataVersion.DefaultMaxTenants) : ITenantDataVersion
{
    public const int DefaultMaxTenants = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (long Stamp, long Touched)> _stamps = [];
    private long _next;
    private long _tick;

    public int TrackedTenants
    {
        get { lock (_gate) { return _stamps.Count; } }
    }

    public long Get(Guid tenantId)
    {
        lock (_gate)
        {
            if (_stamps.TryGetValue(tenantId, out var e))
            {
                _stamps[tenantId] = (e.Stamp, ++_tick);
                return e.Stamp;
            }

            return Set(tenantId);
        }
    }

    public void Bump(Guid tenantId)
    {
        lock (_gate)
        {
            Set(tenantId);
        }
    }

    private long Set(Guid tenantId)
    {
        if (_stamps.Count >= maxTenants && !_stamps.ContainsKey(tenantId))
        {
            // Least recently touched goes; its next read gets a new stamp, so nothing cached under the old one can be served.
            var oldest = _stamps.MinBy(kv => kv.Value.Touched).Key;
            _stamps.Remove(oldest);
        }

        var stamp = ++_next;
        _stamps[tenantId] = (stamp, ++_tick);
        return stamp;
    }
}

/// <summary>
/// Small, bounded, per-tenant cache for expensive read results (dashboard insights). Keys always include the tenant id AND the tenant's
/// <see cref="ITenantDataVersion"/> stamp, so one tenant can never be served another's payload and any write to the tenant's data
/// invalidates its entries. Entries also expire after <see cref="Ttl"/> (writes that bypass SaveChanges, other replicas) and the
/// whole cache holds at most <c>maxEntries</c> items, evicting the oldest.
/// </summary>
public sealed class TenantResultCache(ITenantDataVersion versions, TimeProvider? time = null, int maxEntries = TenantResultCache.DefaultMaxEntries, TimeSpan? ttl = null)
{
    public const int DefaultMaxEntries = 200;

    private readonly object _gate = new();
    private readonly Dictionary<(Guid Tenant, string Key, long Version), (object Value, DateTimeOffset Expires, long Order)> _items = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _order;

    public TimeSpan Ttl { get; } = ttl ?? TimeSpan.FromSeconds(60);

    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }

    public async Task<T> GetOrAddAsync<T>(Guid tenantId, string key, Func<Task<T>> factory)
    {
        var version = versions.Get(tenantId);
        var slot = (tenantId, key, version);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_items.TryGetValue(slot, out var hit) && hit.Expires > now)
            {
                return (T)hit.Value;
            }
        }

        var value = await factory().ConfigureAwait(false);

        // A write that landed while the factory ran made `version` stale: do not cache a result computed from older data.
        if (versions.Get(tenantId) != version)
        {
            return value;
        }

        lock (_gate)
        {
            if (_items.Count >= maxEntries)
            {
                foreach (var stale in _items.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
                {
                    _items.Remove(stale);
                }

                while (_items.Count >= maxEntries)
                {
                    _items.Remove(_items.MinBy(kv => kv.Value.Order).Key);
                }
            }

            _items[slot] = (value!, now + Ttl, ++_order);
        }

        return value;
    }
}
