namespace FlowLedger.Application.Features.Query;

/// <summary>
/// Caps concurrent natural-language queries per tenant (#168): each <c>/ask</c> can hold a local-model call for minutes, so a
/// handful of parallel requests from one tenant would otherwise monopolise the model. Entries exist only while a request is in
/// flight (the count is removed at zero), so the map is bounded by the number of concurrent asks, not by tenants ever seen.
/// Process-local: with several API replicas the effective limit is per replica.
/// </summary>
public sealed class AskConcurrencyGate(int maxPerTenant = AskConcurrencyGate.DefaultMaxPerTenant)
{
    public const int DefaultMaxPerTenant = 2;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _inFlight = [];

    /// <summary>Number of tenants with at least one request in flight (for tests and diagnostics).</summary>
    public int TrackedTenants
    {
        get { lock (_gate) { return _inFlight.Count; } }
    }

    /// <summary>Returns a lease to dispose when the request ends, or null when the tenant is already at its limit.</summary>
    public IDisposable? TryEnter(Guid tenantId)
    {
        lock (_gate)
        {
            _inFlight.TryGetValue(tenantId, out var current);
            if (current >= maxPerTenant)
            {
                return null;
            }

            _inFlight[tenantId] = current + 1;
        }

        return new Lease(this, tenantId);
    }

    private void Release(Guid tenantId)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(tenantId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                _inFlight.Remove(tenantId);
            }
            else
            {
                _inFlight[tenantId] = current - 1;
            }
        }
    }

    private sealed class Lease(AskConcurrencyGate owner, Guid tenantId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(tenantId);
            }
        }
    }
}
