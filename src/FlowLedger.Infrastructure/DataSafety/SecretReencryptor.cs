using FlowLedger.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Counts from one <see cref="SecretReencryptor.RunAsync"/> run.</summary>
public sealed record ReencryptResult(int Resealed, int AlreadyCurrent, int Unreadable);

/// <summary>
/// Key-rotation maintenance (#156): rewrites every stored secret (Home Assistant webhooks, integration credentials) as a v2
/// envelope under the current key, so the previous / legacy key can be retired. Values that cannot be opened with any configured
/// key are left untouched and counted, never overwritten. Idempotent: values already sealed under the current key are skipped.
/// </summary>
public sealed class SecretReencryptor
{
    private readonly string _connectionString;
    private readonly SecretKeyRing _ring;

    internal SecretReencryptor(string connectionString, SecretKeyRing ring)
    {
        _connectionString = connectionString;
        _ring = ring;
    }

    /// <summary>Builds one from configuration using the same key resolution as the stores.</summary>
    public static SecretReencryptor FromConfiguration(IConfiguration configuration, string connectionString) =>
        new(connectionString, DependencyInjection.SecretsKeyRing(configuration, "Integrations:EncryptionKey", "HomeAssistant:EncryptionKey"));

    public async Task<ReencryptResult> RunAsync(CancellationToken ct = default)
    {
        if (!_ring.CanSeal)
        {
            throw new InvalidOperationException("No encryption key is configured; there is nothing to re-encrypt under.");
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        int resealed = 0, current = 0, unreadable = 0;

        var rows = new List<(string Table, Guid Tenant, Guid? Row, string Purpose, string Stored)>();
        if (await TableExistsAsync(conn, "home_assistant_settings", ct))
        {
            await using var cmd = new NpgsqlCommand("SELECT tenant_id, webhook FROM home_assistant_settings WHERE webhook IS NOT NULL", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(("home_assistant_settings", r.GetGuid(0), null, EfHomeAssistantStore.Purpose, r.GetString(1)));
            }
        }

        if (await TableExistsAsync(conn, "integrations", ct))
        {
            await using var cmd = new NpgsqlCommand("SELECT tenant_id, id, secrets FROM integrations WHERE secrets IS NOT NULL", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(("integrations", r.GetGuid(0), r.GetGuid(1), EfIntegrationStore.Purpose, r.GetString(2)));
            }
        }

        foreach (var (table, tenant, row, purpose, stored) in rows)
        {
            if (!SecretBox.NeedsReseal(stored, _ring))
            {
                current++;
                continue;
            }

            var next = SecretBox.Reseal(stored, _ring, SecretBox.Aad(tenant, purpose, row));
            if (next is null)
            {
                unreadable++;
                continue;
            }

            // Optimistic: only replace the exact value that was read, so a concurrent save is never overwritten.
            var sql = table == "home_assistant_settings"
                ? "UPDATE home_assistant_settings SET webhook = @n WHERE tenant_id = @t AND webhook = @o"
                : "UPDATE integrations SET secrets = @n WHERE tenant_id = @t AND id = @r AND secrets = @o";
            await using var upd = new NpgsqlCommand(sql, conn);
            upd.Parameters.AddWithValue("n", next);
            upd.Parameters.AddWithValue("o", stored);
            upd.Parameters.AddWithValue("t", tenant);
            if (row is { } id)
            {
                upd.Parameters.AddWithValue("r", id);
            }

            resealed += await upd.ExecuteNonQueryAsync(ct);
        }

        return new ReencryptResult(resealed, current, unreadable);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("t", "public." + table);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
