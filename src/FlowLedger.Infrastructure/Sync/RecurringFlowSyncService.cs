using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.RecurringFlows;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// After each successful sync, adds the obvious recurring flows (fixed amount, very regular) so forecasts and upcoming
/// lists stay current without the user doing anything. Everything less certain waits as a suggestion. A failure here never
/// fails the sync.
/// </summary>
public sealed class RecurringFlowSyncService(
    IFinancialSyncService inner,
    RecurringSuggestionService suggestions,
    bool autoAdd,
    ILogger<RecurringFlowSyncService> logger) : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = await inner.SyncAsync(ct);
        if (!autoAdd)
        {
            return result;
        }

        try
        {
            var added = await suggestions.AutoAddAsync(ct);
            if (added.Added > 0)
            {
                logger.LogInformation("Auto-added {Count} recurring flow(s): {Names}", added.Added, string.Join(", ", added.Names));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Recurring-flow detection after sync failed; it will retry on the next sync.");
        }

        return result;
    }
}
