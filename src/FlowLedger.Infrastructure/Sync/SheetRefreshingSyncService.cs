using FlowLedger.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Runs the bill-sheet auto-marker after every successful sync, whether it was started by the API,
/// an on-demand job or the scheduled Worker. A marker failure never fails the sync itself.
/// </summary>
internal sealed class SheetRefreshingSyncService(
    IFinancialSyncService inner,
    ISheetAutoMarker? marker,
    ILogger<SheetRefreshingSyncService> logger) : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = await inner.SyncAsync(ct);
        if (marker is not null)
        {
            try
            {
                await marker.RefreshAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Bill sheet refresh after sync failed; it will retry on the next sync.");
            }
        }

        return result;
    }
}
