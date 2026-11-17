using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Runs merchant aliasing, auto-categorization, then name-suggestion + auto-tagging after each successful sync. A failure in either never fails the sync.
/// </summary>
public sealed class CategorizingSyncService(
    IFinancialSyncService inner,
    CategorizationService categorizer,
    MerchantAliasService aliases,
    CategoryRuleService rules,
    TransactionEnrichmentService enrichment,
    ILogger<CategorizingSyncService> logger) : IFinancialSyncService
{
    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var result = await inner.SyncAsync(ct);
        try
        {
            var renamed = await aliases.ApplyAsync(ct);
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
            var ruled = await rules.ApplyAsync(ct);
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

        try
        {
            var e = await enrichment.RunAsync(ct);
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
