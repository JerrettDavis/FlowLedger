using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// After each successful sync, adds the obvious recurring flows (fixed amount, very regular) so forecasts and upcoming
/// lists stay current without the user doing anything. Everything less certain waits as a suggestion. A failure here never
/// fails the sync.
/// </summary>
internal sealed partial class RecurringFlowSyncService(
    IFinancialSyncService inner,
    FlowLedgerDbContext db,
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
            var added = await ConcurrencyRetry.RunAsync(db, () => suggestions.AutoAddAsync(ct), logger, "Recurring-flow detection", ct);
            if (added.Added > 0)
            {
                LogAutoAddedRecurringFlowS(logger, added.Added, added.Names);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecurringFlowDetectionAfterSync(logger, ex);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-added {Count} recurring flow(s): {Names}")]
    private static partial void LogAutoAddedRecurringFlowS(ILogger logger, int count, IEnumerable<string> names);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring-flow detection after sync failed; it will retry on the next sync.")]
    private static partial void LogRecurringFlowDetectionAfterSync(ILogger logger, Exception exception);
}
