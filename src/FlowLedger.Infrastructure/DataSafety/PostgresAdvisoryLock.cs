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

    /// <summary>
    /// Waits (polling) until the named lock is free, e.g. so concurrent starters run migrations one at a time.
    /// </summary>
    /// <exception cref="TimeoutException">The lock was not obtained within <paramref name="timeout"/>.</exception>
    public static async Task<PostgresAdvisoryLock> AcquireAsync(
        string connectionString, string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var handle = await TryAcquireAsync(connectionString, name, ct);
            if (handle is not null)
            {
                return handle;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for advisory lock '{name}'.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    /// <returns>A handle holding the lock, or null when another session already holds it.</returns>
    public static async Task<PostgresAdvisoryLock?> TryAcquireAsync(string connectionString, string name, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
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
        catch
        {
            // Never leak the connection: an orphaned session would keep the lock and block every later sync.
            // Dropping the connection makes Postgres release any lock it may already have granted.
            await conn.DisposeAsync();
            throw;
        }
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
