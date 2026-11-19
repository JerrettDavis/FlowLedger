using System.Security.Cryptography;
using FlowLedger.Application.Features.HomeAssistant;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Per-tenant Home Assistant sharing settings plus the "already sent" ledger. Created on first use like the other ad-hoc
/// tables (no EF migration) and included in every snapshot. The webhook URL is a secret: it is encrypted at rest with
/// AES-256-GCM when <c>HomeAssistant:EncryptionKey</c> (base64, 32 bytes) is configured, is never returned by any read model,
/// and the ledger stores only opaque dedupe keys, never amounts.
/// </summary>
public sealed class NpgsqlHomeAssistantStore : IHomeAssistantStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS home_assistant_settings (
            tenant_id uuid PRIMARY KEY,
            enabled boolean NOT NULL DEFAULT false,
            webhook text NULL,
            share_amounts boolean NOT NULL DEFAULT false,
            include_names boolean NOT NULL DEFAULT true,
            lead_days integer NOT NULL DEFAULT 3,
            kinds text NOT NULL DEFAULT '',
            last_sent_utc timestamptz NULL,
            last_error text NULL,
            updated_utc timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS home_assistant_sent (
            tenant_id uuid NOT NULL,
            dedupe_key text NOT NULL,
            sent_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, dedupe_key)
        );
        """;

    private readonly string _connectionString;
    private readonly byte[]? _key;

    public NpgsqlHomeAssistantStore(string connectionString, string? base64Key = null)
    {
        _connectionString = connectionString;
        _key = SecretBox.ParseKey(base64Key);
    }

    public async Task<HomeAssistantSettings> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT enabled, webhook, share_amounts, include_names, lead_days, kinds, last_sent_utc, last_error " +
            "FROM home_assistant_settings WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return HomeAssistantSettings.Default;
        }

        var stored = r.IsDBNull(1) ? null : r.GetString(1);
        var url = stored is null ? null : SecretBox.Open(stored, _key);
        var kinds = r.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(HaEventKinds.All.Contains).ToList();
        return new HomeAssistantSettings(
            r.GetBoolean(0), stored is not null, HomeAssistantWebhookUrl.DisplayHost(url), r.GetBoolean(2), r.GetBoolean(3), r.GetInt32(4),
            kinds.Count == 0 ? HaEventKinds.All : kinds,
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7));
    }

    public async Task<string?> GetWebhookUrlAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT webhook FROM home_assistant_settings WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteScalarAsync(ct) is string stored ? SecretBox.Open(stored, _key) : null;
    }

    public async Task SaveAsync(Guid tenantId, bool enabled, string? webhookUrl, bool clearWebhook, bool shareAmounts, bool includeNames,
        int leadDays, IReadOnlyList<string> kinds, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO home_assistant_settings (tenant_id, enabled, webhook, share_amounts, include_names, lead_days, kinds) " +
            "VALUES (@t, @e, @w, @s, @n, @l, @k) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET enabled = EXCLUDED.enabled, " +
            "webhook = CASE WHEN @clear THEN NULL WHEN @hasw THEN EXCLUDED.webhook ELSE home_assistant_settings.webhook END, " +
            "share_amounts = EXCLUDED.share_amounts, include_names = EXCLUDED.include_names, lead_days = EXCLUDED.lead_days, " +
            "kinds = EXCLUDED.kinds, updated_utc = now()", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("e", enabled);
        cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = webhookUrl is null ? DBNull.Value : SecretBox.Seal(webhookUrl, _key),
        });
        cmd.Parameters.AddWithValue("clear", clearWebhook);
        cmd.Parameters.AddWithValue("hasw", webhookUrl is not null);
        cmd.Parameters.AddWithValue("s", shareAmounts);
        cmd.Parameters.AddWithValue("n", includeNames);
        cmd.Parameters.AddWithValue("l", leadDays);
        cmd.Parameters.AddWithValue("k", string.Join(',', kinds));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryClaimAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO home_assistant_sent (tenant_id, dedupe_key) VALUES (@t, @k) ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", dedupeKey);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task ReleaseAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM home_assistant_sent WHERE tenant_id = @t AND dedupe_key = @k", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("k", dedupeKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordResultAsync(Guid tenantId, string? error, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE home_assistant_settings SET last_error = @e, last_sent_utc = CASE WHEN @e IS NULL THEN now() ELSE last_sent_utc END " +
            "WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PruneAsync(Guid tenantId, DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM home_assistant_sent WHERE tenant_id = @t AND sent_utc < @o", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("o", olderThan);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(CreateSqlText, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }
}

/// <summary>Tiny AES-256-GCM envelope for stored secrets. "v1:" = encrypted, "p:" = stored without a key configured.</summary>
internal static class SecretBox
{
    public static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        var key = Convert.FromBase64String(base64.Trim());
        return key.Length == 32 ? key : throw new InvalidOperationException("HomeAssistant:EncryptionKey must be 32 bytes, base64 encoded.");
    }

    public static string Seal(string plain, byte[]? key)
    {
        if (key is null)
        {
            return "p:" + plain;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, data, cipher, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Returns null when an encrypted value cannot be opened (key missing or changed), never throws.</summary>
    public static string? Open(string stored, byte[]? key)
    {
        if (stored.StartsWith("p:", StringComparison.Ordinal))
        {
            return stored[2..];
        }

        if (!stored.StartsWith("v1:", StringComparison.Ordinal) || key is null)
        {
            return null;
        }

        try
        {
            var raw = Convert.FromBase64String(stored[3..]);
            var plain = new byte[raw.Length - 28];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
