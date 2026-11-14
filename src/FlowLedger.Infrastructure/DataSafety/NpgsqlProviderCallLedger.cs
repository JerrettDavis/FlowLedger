using FlowLedger.Integrations.Abstractions;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Postgres-backed <see cref="IProviderCallLedger"/>. The table is created on first use (IF NOT EXISTS) so
/// it needs no EF migration; rows are append-only and are part of every snapshot/backup.
/// </summary>
public sealed class NpgsqlProviderCallLedger : IProviderCallLedger
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS provider_api_calls (
            id bigserial PRIMARY KEY,
            provider text NOT NULL,
            method text NOT NULL,
            path text NOT NULL,
            status_code int NULL,
            duration_ms bigint NOT NULL,
            response_bytes bigint NULL,
            occurred_utc timestamptz NOT NULL,
            error text NULL
        );
        CREATE INDEX IF NOT EXISTS ix_provider_api_calls_provider_time ON provider_api_calls (provider, occurred_utc);
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public NpgsqlProviderCallLedger(string connectionString) => _connectionString = connectionString;

    public async Task RecordAsync(ProviderCallRecord call, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO provider_api_calls (provider, method, path, status_code, duration_ms, response_bytes, occurred_utc, error) " +
            "VALUES (@p, @m, @path, @s, @d, @b, @t, @e)", conn);
        cmd.Parameters.AddWithValue("p", call.Provider);
        cmd.Parameters.AddWithValue("m", call.Method);
        cmd.Parameters.AddWithValue("path", call.Path);
        cmd.Parameters.AddWithValue("s", (object?)call.StatusCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("d", call.DurationMs);
        cmd.Parameters.AddWithValue("b", (object?)call.ResponseBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", call.OccurredUtc);
        cmd.Parameters.AddWithValue("e", (object?)call.Error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CountSinceAsync(string provider, DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM provider_api_calls WHERE provider = @p AND occurred_utc >= @t", conn);
        cmd.Parameters.AddWithValue("p", provider);
        cmd.Parameters.AddWithValue("t", sinceUtc);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Usage summary for the UI: calls today / this month and the per-endpoint split for this month.</summary>
    public async Task<ProviderUsageSummary> GetUsageAsync(string provider, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var day = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);
        var month = new DateTimeOffset(new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc));
        var today = await CountSinceAsync(provider, day, ct);
        var monthly = await CountSinceAsync(provider, month, ct);

        var byPath = new List<ProviderEndpointUsage>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT method, path, count(*) FROM provider_api_calls WHERE provider = @p AND occurred_utc >= @t " +
            "GROUP BY method, path ORDER BY count(*) DESC LIMIT 25", conn);
        cmd.Parameters.AddWithValue("p", provider);
        cmd.Parameters.AddWithValue("t", month);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            byPath.Add(new ProviderEndpointUsage(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        }

        return new ProviderUsageSummary(provider, today, monthly, byPath);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        if (!_initialised)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (!_initialised)
                {
                    await using var cmd = new NpgsqlCommand(CreateSql, conn);
                    await cmd.ExecuteNonQueryAsync(ct);
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

public sealed record ProviderEndpointUsage(string Method, string Path, long Calls);

public sealed record ProviderUsageSummary(string Provider, long CallsToday, long CallsThisMonth, IReadOnlyList<ProviderEndpointUsage> ByEndpoint);
