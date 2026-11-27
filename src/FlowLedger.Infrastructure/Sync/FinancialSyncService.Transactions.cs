using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

// Transaction ingestion: page loop, fingerprints, pending settlement, quota (#103).
internal sealed partial class FinancialSyncService
{
    private async Task<(int added, int skipped)> SyncTransactionsAsync(
        Account account,
        string providerAccountId,
        TenantId tenantId,
        CancellationToken ct)
    {
        int added = 0;
        int skipped = 0;

        // Load durable cursor from the store; returns empty string if no prior sync
        var storedCursorValue = await _cursorStore.GetAsync(_provider.ProviderName, providerAccountId, ct);
        var cursor = string.IsNullOrEmpty(storedCursorValue) ? SyncCursor.Initial : new SyncCursor(storedCursorValue);

        // Pending rows that may have been replaced by a posted record under a new provider id.
        // Loaded once per account; rows this sync inserts as pending are added to the lookup below.
        var settleCandidates = new Dictionary<(TransactionDirection, decimal), List<Transaction>>();
        foreach (var p in await _db.Transactions
                     .Where(t => t.AccountId == account.AccountId && t.Status == TransactionStatus.Pending
                         && t.Source == TransactionSource.MxAggregation)
                     .ToListAsync(ct))
        {
            PendingSettlement.AddSettleCandidate(settleCandidates, p);
        }

        // Rows added earlier in this sync are not in the database yet, so a later page repeating one of them would
        // otherwise be inserted again and hit the unique (tenant, fingerprint) index.
        var insertedByFingerprint = new Dictionary<string, Transaction>();

        // Pending fingerprints the provider (re)reported during this sync; only the others can be considered stale.
        var seenPending = new HashSet<string>(StringComparer.Ordinal);

        // The provider path obeys the same per-tenant ceiling as manual create and import (#166).
        var stored = await _db.Transactions.CountAsync(ct);

        bool hasMore = true;
        while (hasMore)
        {
            var page = await _provider.GetTransactionsAsync(providerAccountId, cursor, pageSize: 100, ct);

            if (page.Items.Count > 0)
            {
                // Build fingerprints for all items in this page
                // Banks occasionally report $0.00 informational rows (holds, fee reversals). The domain forbids
                // zero-amount transactions, and one such row must never abort the whole sync, so skip them.
                var zeroAmount = page.Items.Count(pt => pt.Amount.Amount == 0m);
                if (zeroAmount > 0)
                {
                    LogSkippingZeroAmountTransactionS(_logger, zeroAmount);
                    skipped += zeroAmount;
                }

                // A row in another currency than its account would be stored with the account's currency semantics lost
                // (or rejected by the domain); skip it loudly instead of corrupting totals (#124).
                var accountCurrency = account.CurrentBalance.Currency.Code;
                var foreignCurrency = page.Items.Count(pt => pt.Amount.Amount != 0m
                    && !string.Equals(pt.Amount.Currency.Code, accountCurrency, StringComparison.OrdinalIgnoreCase));
                if (foreignCurrency > 0)
                {
                    LogSkippingTransactionSWhoseCurrency(_logger, foreignCurrency, accountCurrency);
                    skipped += foreignCurrency;
                }

                var fingerprintMap = page.Items
                    .Where(pt => pt.Amount.Amount != 0m
                        && string.Equals(pt.Amount.Currency.Code, accountCurrency, StringComparison.OrdinalIgnoreCase))
                    .Select(pt => (
                        ProviderTx: pt,
                        Fingerprint: BuildFingerprint(account.AccountId, pt)))
                    .ToList();

                // One page can repeat a transaction (overlapping backfill windows, a bank listing it twice). Keep a single
                // copy per fingerprint, preferring the posted version over the pending one.
                var distinct = fingerprintMap
                    .GroupBy(x => x.Fingerprint.Value)
                    .Select(g => g.FirstOrDefault(x => !x.ProviderTx.IsPending) is { ProviderTx: not null } posted ? posted : g.First())
                    .ToList();
                var repeatedInPage = fingerprintMap.Count - distinct.Count;
                fingerprintMap = distinct;

                var candidateFingerprints = fingerprintMap
                    .Select(x => x.Fingerprint.Value)
                    .ToList();

                // Batch-load existing rows (tracked) so pending rows can be settled in place.
                var existingRows = await _db.Transactions
                    .Where(t => t.Fingerprint != null && candidateFingerprints.Contains(t.Fingerprint.Value))
                    .ToListAsync(ct);
                var existingByFp = existingRows.ToDictionary(t => t.Fingerprint!.Value);
                // Rows merged away as duplicates are never re-created from the provider.
                var mergedAway = await FlowLedger.Infrastructure.Persistence.Repositories.DuplicateStore.TombstonedAsync(_db, candidateFingerprints, ct);

                var toInsert = new List<Transaction>();
                var settled = 0;

                // Register the whole page's pending rows first: a pending authorization the provider still reports is alive
                // and must never be absorbed by a different posted row (#104).
                foreach (var live in fingerprintMap.Where(f => f.ProviderTx.IsPending))
                {
                    seenPending.Add(live.Fingerprint.Value);
                }

                foreach (var x in fingerprintMap)
                {

                    if (mergedAway.Contains(x.Fingerprint.Value))
                    {
                        continue;
                    }

                    if (insertedByFingerprint.TryGetValue(x.Fingerprint.Value, out var insertedRow))
                    {
                        // Already added earlier in this sync (not saved yet): settle it in place, never insert a second copy.
                        if (!x.ProviderTx.IsPending && insertedRow.Settle(x.ProviderTx.PostedDate))
                        {
                            settled++;
                        }
                        continue;
                    }

                    if (existingByFp.TryGetValue(x.Fingerprint.Value, out var row))
                    {
                        // Same provider id seen before: pending -> posted transition in place.
                        if (!x.ProviderTx.IsPending && row.Settle(x.ProviderTx.PostedDate) && _changed.Track(row))
                        {
                            settled++;
                        }
                        continue;
                    }

                    if (!x.ProviderTx.IsPending)
                    {
                        // Posted record with a new id: absorb the matching pending row (same amount
                        // and direction, pending date within a week before posting) instead of duplicating.
                        var amount = Math.Abs(x.ProviderTx.Amount.Amount);
                        var direction = x.ProviderTx.Amount.Amount < 0m
                            ? TransactionDirection.Debit : TransactionDirection.Credit;
                        // Only absorb on an unambiguous match; with 0 or 2+ candidates insert instead
                        // (a duplicate is recoverable, a wrong merge is silent data loss).
                        var matches = settleCandidates.TryGetValue((direction, amount), out var bucket)
                            ? bucket
                                .Where(t => t.Status == TransactionStatus.Pending
                                    && t.EffectiveDate <= x.ProviderTx.PostedDate
                                    && t.EffectiveDate >= x.ProviderTx.PostedDate.AddDays(-7))
                                .Take(2)
                                .ToList()
                            : [];
                        var pending = matches.Count == 1 ? matches[0] : null;
                        var finalAmount = (Money?)null;
                        if (pending is null && matches.Count == 0)
                        {
                            // Posted total differs from the authorization (a tip): absorb one unambiguous pending row
                            // of the same merchant whose amount is within tolerance below the posted total (#104).
                            var tolerant = PendingSettlement.FindTipSettleCandidate(settleCandidates, direction, amount, x.ProviderTx, seenPending);
                            if (tolerant is not null)
                            {
                                pending = tolerant;
                                finalAmount = new Money(amount, x.ProviderTx.Amount.Currency);
                            }
                        }

                        if (pending is not null && pending.Settle(x.ProviderTx.PostedDate, x.Fingerprint, finalAmount) && _changed.Track(pending))
                        {
                            PendingSettlement.RemoveSettleCandidate(settleCandidates, pending);
                            settled++;
                            continue;
                        }
                    }

                    var mapped = MapToTransaction(x.ProviderTx, account.AccountId, tenantId, x.Fingerprint, _clock);
                    toInsert.Add(mapped);
                    insertedByFingerprint[x.Fingerprint.Value] = mapped;
                }

                if (toInsert.Count > 0)
                {
                    EnsureQuotaRoom(stored, toInsert.Count);
                    stored += toInsert.Count;
                    await _db.Transactions.AddRangeAsync(toInsert, ct);
                    _changed.AddRange(toInsert);
                    added += toInsert.Count;
                    foreach (var inserted in toInsert.Where(t => t.AccountId == account.AccountId
                                 && t.Status == TransactionStatus.Pending && t.Source == TransactionSource.MxAggregation))
                    {
                        PendingSettlement.AddSettleCandidate(settleCandidates, inserted);
                    }
                }
                if (settled > 0)
                {
                    LogSettledPendingTransactionsDuringSync(_logger, settled);
                }
                skipped += repeatedInPage + fingerprintMap.Count - toInsert.Count - settled;
            }

            if (page.SkippedCount > 0)
            {
                skipped += page.SkippedCount;
                LogTheProviderDroppedRowS(_logger, page.SkippedCount);
            }

            cursor = page.NextCursor;
            hasMore = page.HasMore;

            // Persist cursor after each successful page so a mid-sync crash resumes
            // from the last committed position rather than re-fetching from the beginning.
            await _cursorStore.SetAsync(_provider.ProviderName, providerAccountId, cursor.Value, ct);

            // Everything above is now committed. Keep only rows that can still change in this sync (pending ones) tracked;
            // otherwise a long backfill grows the change tracker to the whole history and every SaveChanges rescans it (#163).
            DetachCommittedTransactions(insertedByFingerprint);
        }

        // Authorizations the provider never settled or re-reported (cancelled holds, fuel pre-auths) must not count as
        // spend forever. Anything still pending after the TTL that this sync did not just receive is retired.
        var staleBefore = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-PendingSettlement.StalePendingDays);
        foreach (var list in settleCandidates.Values)
        {
            foreach (var stale in list.Where(t => t.Status == TransactionStatus.Pending && t.EffectiveDate < staleBefore
                         && (t.Fingerprint is null || !seenPending.Contains(t.Fingerprint.Value))).ToList())
            {
                if (stale.AbandonPending())
                {
                    LogRetiredAStalePendingTransaction(_logger, stale.EffectiveDate);
                }
            }
        }

        return (added, skipped);
    }

    private void EnsureQuotaRoom(int stored, int adding) => SyncQuotaGuard.Ensure(_logger, stored, adding, MaxTransactionsPerTenant);

    /// <summary>Stops tracking saved transactions that cannot change again during this sync (anything not Pending).</summary>
    private void DetachCommittedTransactions(Dictionary<string, Transaction> insertedByFingerprint)
    {
        foreach (var entry in _db.ChangeTracker.Entries<Transaction>().ToList())
        {
            if (entry.State == EntityState.Unchanged && entry.Entity.Status != TransactionStatus.Pending)
            {
                if (entry.Entity.Fingerprint is { } fp)
                {
                    insertedByFingerprint.Remove(fp.Value);
                }

                entry.State = EntityState.Detached;
            }
        }
    }

    private static TransactionFingerprint BuildFingerprint(AccountId accountId, ProviderTransaction pt)
    {
        var normalizedDesc = TransactionFingerprint.NormalizeDescription(pt.RawDescription);
        return TransactionFingerprint.Create(
            accountId,
            pt.PostedDate,
            Math.Abs(pt.Amount.Amount),
            normalizedDesc,
            pt.ProviderId);
    }

    private static Transaction MapToTransaction(
        ProviderTransaction pt,
        AccountId accountId,
        TenantId tenantId,
        TransactionFingerprint fingerprint,
        TimeProvider? clock)
    {
        // Signed amount from provider: negative = debit, positive = credit
        var direction = pt.Amount.Amount < 0m
            ? TransactionDirection.Debit
            : TransactionDirection.Credit;

        var amount = new Money(Math.Abs(pt.Amount.Amount), new Currency(pt.Amount.Currency.Code));
        var description = string.IsNullOrWhiteSpace(pt.MerchantName) ? pt.RawDescription : pt.MerchantName;

        return Transaction.RecordActual(
            tenantId,
            accountId,
            amount,
            direction,
            description,
            effectiveDate: pt.PostedDate,
            postedDate: pt.IsPending ? null : pt.PostedDate,
            source: TransactionSource.MxAggregation,
            fingerprint: fingerprint,
            time: clock);
    }
}
