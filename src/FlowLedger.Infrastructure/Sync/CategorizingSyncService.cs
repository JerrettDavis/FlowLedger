using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>Runs auto-categorization after each successful sync. A categorization failure never fails the sync.</summary>
public sealed class CategorizingSyncService(
    IFinancialSyncService inner,
    CategorizationService categorizer,
    ILogger<CategorizingSyncService> logger) : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = await inner.SyncAsync(ct);
        try
        {
            var c = await categorizer.RunAsync(ct);
            if (c.Categorized > 0)
            {
                logger.LogInformation("Auto-categorized {Count} transactions ({Learned} learned, {Keyword} keyword, {Transfers} transfers).",
                    c.Categorized, c.ByLearnedMerchant, c.ByKeyword, c.Transfers);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Auto-categorization after sync failed; it will retry on the next sync.");
        }

        return result;
    }
}
