using FlowLedger.Application.Features.Duplicates;
using FlowLedger.Domain.Aggregates;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Duplicate-handling persistence. The two small tables (merged_transactions, duplicate_dismissals) are EF entities with tenant
/// query filters and an EF migration (#154), so backups, exports and restore drills carry them like every other table.
/// </summary>
internal sealed class DuplicateStore(FlowLedgerDbContext db, ITenantContext tenant) : IDuplicateStore
{
    public async Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default)
        => await db.Transactions
            .Where(TransactionStatusExtensions.RealTransaction)
            .OrderByDescending(t => t.EffectiveDate).ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<IReadOnlySet<string>> DismissedKeysAsync(CancellationToken ct = default)
    {
        var keys = await db.DuplicateDismissals.AsNoTracking().Select(d => d.PairKey).ToListAsync(ct);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task DismissAsync(string key, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO duplicate_dismissals (tenant_id, pair_key) VALUES ({tenantId}, {key}) ON CONFLICT DO NOTHING", ct);
    }

    public async Task MergeAsync(Transaction survivor, Transaction duplicate, bool auto, string reason, string snapshotJson, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var fingerprint = duplicate.Fingerprint?.Value;
        // A retrying execution strategy (AddInfrastructure enables one) rejects user transactions that
        // are not wrapped in it, so the whole unit is retried together (#143).
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO merged_transactions (tenant_id, duplicate_id, survivor_id, fingerprint, auto, reason, snapshot)
                   VALUES ({tenantId}, {duplicate.Id}, {survivor.Id}, {fingerprint}, {auto}, {reason}, {snapshotJson}) ON CONFLICT DO NOTHING", ct);
            if (db.Entry(duplicate).State != EntityState.Deleted)
            {
                db.Transactions.Remove(duplicate);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task<IReadOnlyList<MergeLogEntry>> RecentMergesAsync(int take, CancellationToken ct = default)
    {
        var rows = await db.MergedTransactions.AsNoTracking()
            .OrderByDescending(m => m.MergedUtc)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(r => new MergeLogEntry(r.DuplicateId, r.SurvivorId, r.MergedUtc, r.Auto, r.Reason, r.Snapshot)).ToList();
    }

    /// <summary>Which of these fingerprints belong to rows that were merged away (so sync/import must not re-create them).</summary>
    internal static async Task<HashSet<string>> TombstonedAsync(FlowLedgerDbContext db, IReadOnlyCollection<string> fingerprints, CancellationToken ct)
    {
        // Tombstones are per tenant. Without a tenant (design time) nothing can be looked up.
        if (fingerprints.Count == 0 || db.CurrentTenantId is not { } tenantId)
        {
            return [];
        }

        var wanted = fingerprints.ToArray();
        var found = await db.MergedTransactions.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Fingerprint != null && wanted.Contains(m.Fingerprint))
            .Select(m => m.Fingerprint!)
            .Distinct()
            .ToListAsync(ct);
        return found.ToHashSet(StringComparer.Ordinal);
    }
}
