using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>One finished background job run, as persisted.</summary>
public sealed record JobRunEntry(string Job, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, bool Success, string? Error, int Attempt);

/// <summary>Durable history of background job runs (the worker writes it, the API reads it for the Data page).</summary>
public interface IJobRunStore
{
    Task AppendAsync(JobRunEntry run, CancellationToken ct = default);

    /// <summary>Most recent first.</summary>
    Task<IReadOnlyList<JobRunEntry>> RecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// Postgres-backed <see cref="IJobRunStore"/> (<c>job_runs</c>, created on first use like the other ad hoc tables, so it needs no EF
/// migration and is part of every snapshot). Rows older than <see cref="RetentionDays"/> are deleted on insert.
/// </summary>
internal sealed class NpgsqlJobRunStore(string connectionString) : IJobRunStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS job_runs (
            id bigserial PRIMARY KEY,
            job text NOT NULL,
            started_utc timestamptz NOT NULL,
            finished_utc timestamptz NOT NULL,
            success boolean NOT NULL,
            error text NULL,
            attempt int NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS ix_job_runs_finished ON job_runs (finished_utc);
        """;

    public const int RetentionDays = 90;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public async Task AppendAsync(JobRunEntry run, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO job_runs (job, started_utc, finished_utc, success, error, attempt) VALUES (@j, @s, @f, @ok, @e, @a)", conn))
        {
            insert.Parameters.AddWithValue("j", run.Job);
            insert.Parameters.AddWithValue("s", run.StartedUtc);
            insert.Parameters.AddWithValue("f", run.FinishedUtc);
            insert.Parameters.AddWithValue("ok", run.Success);
            insert.Parameters.AddWithValue("e", (object?)Truncate(run.Error, 1000) ?? DBNull.Value);
            insert.Parameters.AddWithValue("a", run.Attempt);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var prune = new NpgsqlCommand("DELETE FROM job_runs WHERE finished_utc < @cutoff", conn);
        prune.Parameters.AddWithValue("cutoff", run.FinishedUtc.AddDays(-RetentionDays));
        await prune.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<JobRunEntry>> RecentAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT job, started_utc, finished_utc, success, error, attempt FROM job_runs ORDER BY id DESC LIMIT @n", conn);
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 500));
        var runs = new List<JobRunEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            runs.Add(new JobRunEntry(
                r.GetString(0),
                r.GetFieldValue<DateTimeOffset>(1),
                r.GetFieldValue<DateTimeOffset>(2),
                r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5)));
        }

        return runs;
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        if (!_initialised)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (!_initialised)
                {
                    await using var create = new NpgsqlCommand(CreateSqlText, conn);
                    await create.ExecuteNonQueryAsync(ct);
                    _initialised = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        return conn;
    }
}
