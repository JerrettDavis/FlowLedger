using System.Collections.Concurrent;
using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Observability;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Innermost sync decorator enforcing API-cost rules at the single entry point used by the API, the on-demand
/// (webhook) job and the Worker: (1) at most one sync per tenant at a time, (2) a minimum interval since the
/// last successful sync. Syncs inside the interval are skipped, never queued, so the provider is not called.
/// </summary>
internal sealed class GuardedSyncService : IFinancialSyncService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private readonly IFinancialSyncService _inner;
    private readonly Guid _tenant;
    private readonly TimeSpan _minInterval;
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _lastSync;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<IAsyncDisposable?>>? _crossProcessLock;

    public GuardedSyncService(
        IFinancialSyncService inner,
        Guid tenant,
        TimeSpan minInterval,
        Func<CancellationToken, Task<DateTimeOffset?>> lastSync,
        TimeProvider? time = null,
        Func<CancellationToken, Task<IAsyncDisposable?>>? crossProcessLock = null)
    {
        _inner = inner;
        _tenant = tenant;
        _minInterval = minInterval;
        _lastSync = lastSync;
        _time = time ?? TimeProvider.System;
        _crossProcessLock = crossProcessLock;
    }

    public Task<string> ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var gate = Locks.GetOrAdd(_tenant, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
        {
            FlowLedgerTelemetry.RecordSync("already_running");
            throw new SyncAlreadyRunningException();
        }

        IAsyncDisposable? lease = null;
        try
        {
            // The in-process gate only stops two syncs in THIS process; the API, the webhook job and the Worker are
            // separate processes, so a database advisory lock is held for the whole sync (null = someone else has it).
            if (_crossProcessLock is not null)
            {
                lease = await _crossProcessLock(ct);
                if (lease is null)
                {
                    FlowLedgerTelemetry.RecordSync("already_running");
                    throw new SyncAlreadyRunningException();
                }
            }

            if (_minInterval > TimeSpan.Zero)
            {
                var last = await _lastSync(ct);
                if (last is { } l)
                {
                    var remaining = l + _minInterval - _time.GetUtcNow();
                    if (remaining > TimeSpan.Zero)
                    {
                        FlowLedgerTelemetry.RecordSync("throttled");
                        throw new SyncThrottledException(remaining, l);
                    }
                }
            }

            var started = _time.GetTimestamp();
            try
            {
                var result = await _inner.SyncAsync(ct);
                FlowLedgerTelemetry.RecordSync("success", _time.GetElapsedTime(started));
                return result;
            }
            catch (Exception)
            {
                FlowLedgerTelemetry.RecordSync("failure", _time.GetElapsedTime(started));
                throw;
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            gate.Release();
        }
    }
}
