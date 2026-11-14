using System.Collections.Concurrent;
using FlowLedger.Application.Abstractions;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Innermost sync decorator enforcing API-cost rules at the single entry point used by the API, the on-demand
/// (webhook) job and the Worker: (1) at most one sync per tenant at a time, (2) a minimum interval since the
/// last successful sync. Syncs inside the interval are skipped, never queued, so the provider is not called.
/// </summary>
public sealed class GuardedSyncService : IFinancialSyncService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private readonly IFinancialSyncService _inner;
    private readonly Guid _tenant;
    private readonly TimeSpan _minInterval;
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _lastSync;
    private readonly TimeProvider _time;

    public GuardedSyncService(
        IFinancialSyncService inner,
        Guid tenant,
        TimeSpan minInterval,
        Func<CancellationToken, Task<DateTimeOffset?>> lastSync,
        TimeProvider? time = null)
    {
        _inner = inner;
        _tenant = tenant;
        _minInterval = minInterval;
        _lastSync = lastSync;
        _time = time ?? TimeProvider.System;
    }

    public Task<string> ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var gate = Locks.GetOrAdd(_tenant, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
        {
            throw new SyncAlreadyRunningException();
        }

        try
        {
            if (_minInterval > TimeSpan.Zero)
            {
                var last = await _lastSync(ct);
                if (last is { } l)
                {
                    var remaining = l + _minInterval - _time.GetUtcNow();
                    if (remaining > TimeSpan.Zero)
                    {
                        throw new SyncThrottledException(remaining, l);
                    }
                }
            }

            return await _inner.SyncAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
