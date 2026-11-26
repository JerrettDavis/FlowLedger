using FlowLedger.Application.Abstractions;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Postgres-backed <see cref="IWebhookReplayStore"/> (#152). One row per (provider, body hash); the insert is atomic, so exactly one of
/// several concurrent deliveries of the same body (on any instance) is treated as new. Holds hashes and timestamps only, no payload and
/// no tenant data; old rows are pruned opportunistically.
/// </summary>
public sealed class NpgsqlWebhookReplayStore(string connectionString) : IWebhookReplayStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS webhook_replays (
            provider text NOT NULL,
            body_hash text NOT NULL,
            seen_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (provider, body_hash)
        );
        CREATE INDEX IF NOT EXISTS ix_webhook_replays_seen ON webhook_replays (seen_utc);
        """;

    private volatile bool _schemaReady;

    public async Task<bool> TryRecordAsync(string provider, string bodyHashHex, TimeSpan window, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        if (!_schemaReady)
        {
            await using var create = new NpgsqlCommand(CreateSqlText, conn);
            await create.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }

        // Inserted (new) or refreshed after expiry => a row is returned. A live duplicate matches the conflict but fails the WHERE => no row.
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO webhook_replays (provider, body_hash, seen_utc) VALUES (@p, @h, now()) " +
            "ON CONFLICT (provider, body_hash) DO UPDATE SET seen_utc = now() WHERE webhook_replays.seen_utc < now() - @w " +
            "RETURNING 1", conn);
        cmd.Parameters.AddWithValue("p", provider);
        cmd.Parameters.AddWithValue("h", bodyHashHex);
        cmd.Parameters.AddWithValue("w", window);
        var isNew = await cmd.ExecuteScalarAsync(ct) is not null;

        if (isNew && Random.Shared.Next(50) == 0)
        {
            await using var prune = new NpgsqlCommand("DELETE FROM webhook_replays WHERE seen_utc < now() - @w", conn);
            prune.Parameters.AddWithValue("w", window * 6);
            await prune.ExecuteNonQueryAsync(ct);
        }

        return isNew;
    }
}
