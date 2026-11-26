using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Counts from one <see cref="DataRetention.PruneAsync"/> run.</summary>
public sealed record RetentionResult(int SnapshotsBlanked, int LinkCodesDeleted);

/// <summary>How long ad-hoc tables keep data before <see cref="DataRetention"/> trims it (<c>Retention:*</c> configuration).</summary>
public sealed record RetentionSettings(TimeSpan MergeSnapshotAge, TimeSpan LinkCodeAge)
{
    public static RetentionSettings Default { get; } = new(TimeSpan.FromDays(90), TimeSpan.FromHours(24));

    /// <summary>Reads <c>Retention:MergeSnapshotDays</c> (default 90) and <c>Retention:LinkCodeGraceHours</c> (default 24); non-positive or missing values fall back to the default.</summary>
    public static RetentionSettings From(IConfiguration configuration)
    {
        var days = configuration.GetValue<int?>("Retention:MergeSnapshotDays");
        var hours = configuration.GetValue<int?>("Retention:LinkCodeGraceHours");
        return new RetentionSettings(
            days is > 0 ? TimeSpan.FromDays(days.Value) : Default.MergeSnapshotAge,
            hours is > 0 ? TimeSpan.FromHours(hours.Value) : Default.LinkCodeAge);
    }
}

/// <summary>
/// Bounded-growth maintenance for tables that would otherwise grow forever (#181). Merge snapshots hold full transaction JSON
/// (financial PII) and are only needed for undo, so they are blanked after a while; the fingerprint tombstone is kept so a
/// re-import can never resurrect a merged row. Link codes are deleted once well past expiry.
/// </summary>
public sealed class DataRetention(string connectionString, RetentionSettings? settings = null)
{
    private readonly RetentionSettings _settings = settings ?? RetentionSettings.Default;

    /// <summary>Prunes using the configured ages.</summary>
    public Task<RetentionResult> PruneAsync(CancellationToken ct = default) => PruneAsync(_settings.MergeSnapshotAge, _settings.LinkCodeAge, ct);

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
