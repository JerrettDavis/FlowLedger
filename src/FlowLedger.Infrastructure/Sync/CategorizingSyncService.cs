using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Runs merchant aliasing, auto-categorization, then name-suggestion + auto-tagging after each successful sync. A failure in either never fails the sync.
/// </summary>
internal sealed partial class CategorizingSyncService(
    IFinancialSyncService inner,
    FlowLedgerDbContext db,
    CategorizationService categorizer,
    MerchantAliasService aliases,
    CategoryRuleService rules,
    TransactionEnrichmentService enrichment,
    ILogger<CategorizingSyncService> logger,
    Guid tenantId = default) : IFinancialSyncService
{
    /// <summary>
    /// Tenants whose last sync failed part-way: rows it already saved were never handed to the id-scoped steps, so the next successful
    /// sync sweeps the whole window once. Process-local and bounded (entries are removed on the next success; capped at <see cref="MaxPendingSweeps"/>).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> PendingFullSweep = new();
    internal const int MaxPendingSweeps = 10_000;

    internal static int PendingFullSweeps => PendingFullSweep.Count;

    public Task<string> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

    // A user edit that lands while a step is writing is not a failure: the step is re-run on fresh rows and respects the edit (#157).
    private Task<T> Retry<T>(string step, Func<Task<T>> unit, CancellationToken ct)
        => ConcurrencyRetry.RunAsync(db, unit, logger, step, ct);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        SyncResult result;
        try
        {
            result = await inner.SyncAsync(ct);
        }
        catch (Exception ex) when (ex is not (SyncThrottledException or SyncAlreadyRunningException) && tenantId != Guid.Empty && PendingFullSweep.Count < MaxPendingSweeps)
        {
            PendingFullSweep[tenantId] = 0;
            throw;
        }

        // Aliases, rules and enrichment only matter for rows this sync inserted or settled: they get exactly those ids (#163) instead of
        // re-reading the history. Unknown ids (a service that does not report them) or a failed earlier sync fall back to the whole window.
        var needsSweep = tenantId != Guid.Empty && PendingFullSweep.TryRemove(tenantId, out _);
        IReadOnlyCollection<Guid>? scope = needsSweep ? null : result.ChangedTransactionIds;
        var hasNew = needsSweep || (scope is null ? result.TransactionsAdded > 0 : scope.Count > 0);
        try
        {
            var renamed = hasNew ? await Retry("Merchant aliases", () => aliases.ApplyToAsync(scope, ct), ct) : 0;
            if (renamed > 0)
            {
                LogAppliedMerchantAliasesToTransactions(logger, renamed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogApplyingMerchantAliasesAfterSync(logger, ex);
        }

        try
        {
            var ruled = hasNew ? await Retry("Categorization rules", () => rules.ApplyToAsync(scope, ct), ct) : 0;
            if (ruled > 0)
            {
                LogAppliedCategorizationRulesToTransactions(logger, ruled);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogApplyingCategorizationRulesAfterSync(logger, ex);
        }

        try
        {
            var c = await Retry("Auto-categorization", () => categorizer.RunAsync(ct), ct);
            if (c.Categorized > 0)
            {
                LogAutoCategorizedTransactionsLearnedKeyword(logger, c.Categorized, c.ByLearnedMerchant, c.ByKeyword, c.Transfers);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAutoCategorizationAfterSyncFailed(logger, ex);
        }

        try
        {
            var e = hasNew ? await Retry("Transaction enrichment", () => enrichment.RunForAsync(scope, ct), ct) : new FlowLedger.Application.Features.Transactions.EnrichmentResult(0, 0);
            if (e.Tagged + e.Named > 0)
            {
                LogEnrichedTransactionsAutoTaggedGiven(logger, e.Tagged, e.Named);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTaggingNameSuggestionAfterSync(logger, ex);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied merchant aliases to {Count} transactions.")]
    private static partial void LogAppliedMerchantAliasesToTransactions(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying merchant aliases after sync failed; it will retry on the next sync.")]
    private static partial void LogApplyingMerchantAliasesAfterSync(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied categorization rules to {Count} transactions.")]
    private static partial void LogAppliedCategorizationRulesToTransactions(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying categorization rules after sync failed; it will retry on the next sync.")]
    private static partial void LogApplyingCategorizationRulesAfterSync(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-categorized {Count} transactions ({Learned} learned, {Keyword} keyword, {Transfers} transfers).")]
    private static partial void LogAutoCategorizedTransactionsLearnedKeyword(ILogger logger, int count, int learned, int keyword, int transfers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-categorization after sync failed; it will retry on the next sync.")]
    private static partial void LogAutoCategorizationAfterSyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enriched transactions: {Tagged} auto-tagged, {Named} given a suggested name.")]
    private static partial void LogEnrichedTransactionsAutoTaggedGiven(ILogger logger, int tagged, int named);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tagging/name suggestion after sync failed; it will retry on the next sync.")]
    private static partial void LogTaggingNameSuggestionAfterSync(ILogger logger, Exception exception);
}
