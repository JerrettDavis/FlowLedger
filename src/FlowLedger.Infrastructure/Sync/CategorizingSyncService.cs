using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Runs merchant aliasing, auto-categorization, then name-suggestion + auto-tagging after each successful sync. A failure in either never fails the sync.
/// </summary>
public sealed class CategorizingSyncService(
    IFinancialSyncService inner,
    FlowLedgerDbContext db,
    CategorizationService categorizer,
    MerchantAliasService aliases,
    CategoryRuleService rules,
    TransactionEnrichmentService enrichment,
    ILogger<CategorizingSyncService> logger) : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    // A user edit that lands while a step is writing is not a failure: the step is re-run on fresh rows and respects the edit (#157).
    private Task<T> Retry<T>(string step, Func<Task<T>> unit, CancellationToken ct)
        => ConcurrencyRetry.RunAsync(db, unit, logger, step, ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = await inner.SyncAsync(ct);
        // Aliases, rules and enrichment only matter for rows this sync brought in; each re-loads the whole history, so skip them on a no-op sync.
        var hasNew = result.TransactionsAdded > 0;
        try
        {
            var renamed = hasNew ? await Retry("Merchant aliases", () => aliases.ApplyAsync(ct), ct) : 0;
            if (renamed > 0)
            {
                logger.LogInformation("Applied merchant aliases to {Count} transactions.", renamed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Applying merchant aliases after sync failed; it will retry on the next sync.");
        }

        try
        {
            var ruled = hasNew ? await Retry("Categorization rules", () => rules.ApplyAsync(ct), ct) : 0;
            if (ruled > 0)
            {
                logger.LogInformation("Applied categorization rules to {Count} transactions.", ruled);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Applying categorization rules after sync failed; it will retry on the next sync.");
        }

        try
        {
            var c = await Retry("Auto-categorization", () => categorizer.RunAsync(ct), ct);
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

        try
        {
            var e = hasNew ? await Retry("Transaction enrichment", () => enrichment.RunAsync(ct), ct) : new FlowLedger.Application.Features.Transactions.EnrichmentResult(0, 0);
            if (e.Tagged + e.Named > 0)
            {
                logger.LogInformation("Enriched transactions: {Tagged} auto-tagged, {Named} given a suggested name.", e.Tagged, e.Named);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Tagging/name suggestion after sync failed; it will retry on the next sync.");
        }

        return result;
    }
}
