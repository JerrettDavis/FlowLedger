using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Duplicates;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// After every successful sync that brought in new rows, folds unambiguous duplicates (High confidence only) before
/// categorization/recurring detection run, so they never see the same purchase twice. Never fails the sync.
/// </summary>
public sealed class DedupingSyncService(
    IFinancialSyncService inner,
    DuplicateService duplicates,
    ILogger<DedupingSyncService> logger) : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = await inner.SyncAsync(ct);
        if (result.TransactionsAdded > 0)
        {
            try
            {
                var r = await duplicates.AutoMergeAsync(ct);
                if (r.Merged > 0 || r.Suggested > 0)
                {
                    logger.LogInformation("Duplicate check after sync: {Merged} merged automatically, {Suggested} left as suggestions.", r.Merged, r.Suggested);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Duplicate merge after sync failed; it will retry on the next sync.");
            }
        }

        return result;
    }
}
