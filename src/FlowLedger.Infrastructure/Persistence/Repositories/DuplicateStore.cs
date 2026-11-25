using FlowLedger.Application.Features.Duplicates;
using FlowLedger.Domain.Aggregates;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Duplicate-handling persistence. The two small tables are created on first use (IF NOT EXISTS, no EF migration) and are
/// listed in <c>AdHocSchema</c>, so backups, exports and restore drills carry them like every other table.
/// </summary>
internal sealed class DuplicateStore(FlowLedgerDbContext db, ITenantContext tenant) : IDuplicateStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS merged_transactions (
            tenant_id uuid NOT NULL,
            duplicate_id uuid NOT NULL,
            survivor_id uuid NOT NULL,
            fingerprint text NULL,
            merged_utc timestamptz NOT NULL DEFAULT now(),
            auto boolean NOT NULL DEFAULT false,
            reason text NOT NULL DEFAULT '',
            snapshot text NOT NULL DEFAULT '',
            PRIMARY KEY (tenant_id, duplicate_id)
        );
        CREATE INDEX IF NOT EXISTS ix_merged_transactions_tenant_fingerprint ON merged_transactions (tenant_id, fingerprint);
        CREATE TABLE IF NOT EXISTS duplicate_dismissals (
            tenant_id uuid NOT NULL,
            pair_key text NOT NULL,
            dismissed_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, pair_key)
        );
        """;

    public async Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default)
        => await db.Transactions
            .Where(t => t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped)
            .OrderByDescending(t => t.EffectiveDate).ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<IReadOnlySet<string>> DismissedKeysAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(CreateSqlText, ct);
        var keys = await db.Database.SqlQueryRaw<string>(
            "SELECT pair_key AS \"Value\" FROM duplicate_dismissals WHERE tenant_id = {0}", tenant.TenantId).ToListAsync(ct);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task DismissAsync(string key, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(CreateSqlText, ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO duplicate_dismissals (tenant_id, pair_key) VALUES ({0}, {1}) ON CONFLICT DO NOTHING", tenant.TenantId, key);
    }

    public async Task MergeAsync(Transaction survivor, Transaction duplicate, bool auto, string reason, string snapshotJson, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(CreateSqlText, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO merged_transactions (tenant_id, duplicate_id, survivor_id, fingerprint, auto, reason, snapshot) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}) ON CONFLICT DO NOTHING",
            tenant.TenantId, duplicate.Id, survivor.Id, (object?)duplicate.Fingerprint?.Value ?? DBNull.Value, auto, reason, snapshotJson);
        db.Transactions.Remove(duplicate);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<MergeLogEntry>> RecentMergesAsync(int take, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(CreateSqlText, ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT duplicate_id, survivor_id, merged_utc, auto, reason, snapshot FROM merged_transactions " +
                "WHERE tenant_id = @t ORDER BY merged_utc DESC LIMIT @n", conn);
            cmd.Parameters.AddWithValue("t", tenant.TenantId);
            cmd.Parameters.AddWithValue("n", take);
            var list = new List<MergeLogEntry>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new MergeLogEntry(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.GetBoolean(3), r.GetString(4), r.GetString(5)));
            }

            return list;
        }
        finally
        {
            if (wasClosed)
            {
                await conn.CloseAsync();
            }
        }
    }

    /// <summary>Which of these fingerprints belong to rows that were merged away (so sync/import must not re-create them).</summary>
    internal static async Task<HashSet<string>> TombstonedAsync(FlowLedgerDbContext db, IReadOnlyCollection<string> fingerprints, CancellationToken ct)
    {
        // Tombstones are per tenant. Without a tenant (design time) nothing can be looked up.
        if (fingerprints.Count == 0 || db.CurrentTenantId is not { } tenantId)
        {
            return [];
        }

        await db.Database.ExecuteSqlRawAsync(CreateSqlText, ct);
        var found = await db.Database.SqlQueryRaw<string>(
            "SELECT DISTINCT fingerprint AS \"Value\" FROM merged_transactions WHERE tenant_id = @t AND fingerprint = ANY(@p)",
            new NpgsqlParameter("t", tenantId), new NpgsqlParameter("p", fingerprints.ToArray())).ToListAsync(ct);
        return found.ToHashSet(StringComparer.Ordinal);
    }
}
