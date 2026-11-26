using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Counts from one <see cref="DataRetention.PruneAsync"/> run.</summary>
public sealed record RetentionResult(int SnapshotsBlanked, int LinkCodesDeleted);

/// <summary>
/// Bounded-growth maintenance for tables that would otherwise grow forever (#181). Merge snapshots hold full transaction JSON
/// (financial PII) and are only needed for undo, so they are blanked after a while; the fingerprint tombstone is kept so a
/// re-import can never resurrect a merged row. Link codes are deleted once well past expiry.
/// </summary>
public sealed class DataRetention(string connectionString)
{
    public async Task<RetentionResult> PruneAsync(TimeSpan snapshotAge, TimeSpan linkCodeAge, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var blanked = 0;
        if (await TableExistsAsync(conn, "merged_transactions", ct))
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE merged_transactions SET snapshot = '' WHERE snapshot <> '' AND merged_utc < now() - @age", conn);
            cmd.Parameters.AddWithValue("age", snapshotAge);
            blanked = await cmd.ExecuteNonQueryAsync(ct);
        }

        var deleted = 0;
        if (await TableExistsAsync(conn, "app_user_link_codes", ct))
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM app_user_link_codes WHERE expires_utc < now() - @age", conn);
            cmd.Parameters.AddWithValue("age", linkCodeAge);
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }

        return new RetentionResult(blanked, deleted);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("t", "public." + table);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
