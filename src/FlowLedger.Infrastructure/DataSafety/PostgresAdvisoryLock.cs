using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Cross-PROCESS mutual exclusion (API, on-demand job and Worker are separate processes): a Postgres session-level
/// advisory lock held on a dedicated connection for as long as the returned handle lives. If the process dies the
/// connection drops and Postgres releases the lock, so it can never be left stuck.
/// </summary>
public sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private int _disposed;

    private PostgresAdvisoryLock(NpgsqlConnection connection, long key)
    {
        _connection = connection;
        _key = key;
    }

    /// <summary>Stable 64-bit key for a name (FNV-1a), so every process derives the same lock id.</summary>
    public static long KeyFor(string name)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return unchecked((long)hash);
    }

    /// <returns>A handle holding the lock, or null when another session already holds it.</returns>
    public static async Task<PostgresAdvisoryLock?> TryAcquireAsync(string connectionString, string name, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        var key = KeyFor(name);
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
        cmd.Parameters.AddWithValue("k", key);
        if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
        {
            return new PostgresAdvisoryLock(conn, key);
        }

        await conn.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // idempotent
        }

        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", _connection);
            cmd.Parameters.AddWithValue("k", _key);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // connection already gone: Postgres has released the lock
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
