using System.Text.Json;
using FlowLedger.Application.Features.Integrations;
using Npgsql;
using NpgsqlTypes;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Per-tenant integration definitions and their delivery log. Created on first use like the other ad-hoc tables (no EF
/// migration) and included in every snapshot. Credentials and webhook signing secrets are encrypted at rest with AES-256-GCM
/// (<c>Integrations:EncryptionKey</c>, falling back to <c>HomeAssistant:EncryptionKey</c>); with no key they are refused rather than
/// stored in clear text. The delivery log holds status and timing only, never request or response bodies.
/// </summary>
public sealed class NpgsqlIntegrationStore : IIntegrationStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS integrations (
            tenant_id uuid NOT NULL,
            id uuid NOT NULL,
            name text NOT NULL,
            kind text NOT NULL,
            enabled boolean NOT NULL DEFAULT true,
            config text NOT NULL,
            secrets text NULL,
            created_utc timestamptz NOT NULL DEFAULT now(),
            last_used_utc timestamptz NULL,
            last_error text NULL,
            PRIMARY KEY (tenant_id, id)
        );
        CREATE TABLE IF NOT EXISTS integration_deliveries (
            tenant_id uuid NOT NULL,
            id uuid NOT NULL,
            integration_id uuid NOT NULL,
            at_utc timestamptz NOT NULL DEFAULT now(),
            trigger text NOT NULL,
            method text NOT NULL,
            target text NOT NULL,
            status_code integer NULL,
            duration_ms integer NOT NULL,
            error text NULL,
            PRIMARY KEY (tenant_id, id)
        );
        CREATE INDEX IF NOT EXISTS ix_integration_deliveries_integration
            ON integration_deliveries (tenant_id, integration_id, at_utc DESC);
        """;

    private const int KeepDeliveries = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly byte[]? _key;

    public NpgsqlIntegrationStore(string connectionString, string? base64Key = null)
    {
        _connectionString = connectionString;
        _key = SecretBox.ParseKey(base64Key);
    }

    public bool CanStoreSecrets => _key is not null;

    public async Task<IReadOnlyList<Integration>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(SelectSql + " WHERE tenant_id = @t ORDER BY lower(name), created_utc", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Integration>();
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public async Task<Integration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(SelectSql + " WHERE tenant_id = @t AND id = @i", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("i", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IntegrationSecrets> GetSecretsAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT secrets FROM integrations WHERE tenant_id = @t AND id = @i", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("i", id);
        return await cmd.ExecuteScalarAsync(ct) is string stored ? OpenSecrets(stored) : new IntegrationSecrets(null, null);
    }

    public async Task SaveAsync(Guid tenantId, Integration integration, string? credential, bool clearCredential, string? signingSecret, CancellationToken ct = default)
    {
        if ((credential is not null || signingSecret is not null) && _key is null)
        {
            throw new InvalidOperationException("Set Integrations:EncryptionKey before storing integration secrets.");
        }

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var current = new IntegrationSecrets(null, null);
        await using (var read = new NpgsqlCommand("SELECT secrets FROM integrations WHERE tenant_id = @t AND id = @i FOR UPDATE", conn, tx))
        {
            read.Parameters.AddWithValue("t", tenantId);
            read.Parameters.AddWithValue("i", integration.Id);
            if (await read.ExecuteScalarAsync(ct) is string stored)
            {
                current = OpenSecrets(stored);
            }
        }

        var next = new IntegrationSecrets(
            clearCredential ? null : credential ?? current.Credential,
            signingSecret ?? current.SigningSecret);
        var sealedSecrets = next is { Credential: null, SigningSecret: null } ? null : SealSecrets(next);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO integrations (tenant_id, id, name, kind, enabled, config, secrets, created_utc) " +
            "VALUES (@t, @i, @n, @k, @e, @c, @s, @cr) " +
            "ON CONFLICT (tenant_id, id) DO UPDATE SET name = EXCLUDED.name, enabled = EXCLUDED.enabled, " +
            "config = EXCLUDED.config, secrets = EXCLUDED.secrets", conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("i", integration.Id);
        cmd.Parameters.AddWithValue("n", integration.Name);
        cmd.Parameters.AddWithValue("k", integration.Kind.ToString());
        cmd.Parameters.AddWithValue("e", integration.Enabled);
        cmd.Parameters.AddWithValue("c", JsonSerializer.Serialize(integration.Config, Json));
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)sealedSecrets ?? DBNull.Value });
        cmd.Parameters.AddWithValue("cr", integration.CreatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var log = new NpgsqlCommand("DELETE FROM integration_deliveries WHERE tenant_id = @t AND integration_id = @i", conn, tx))
        {
            log.Parameters.AddWithValue("t", tenantId);
            log.Parameters.AddWithValue("i", id);
            await log.ExecuteNonQueryAsync(ct);
        }

        int rows;
        await using (var cmd = new NpgsqlCommand("DELETE FROM integrations WHERE tenant_id = @t AND id = @i", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("i", id);
            rows = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return rows > 0;
    }

    public async Task RecordDeliveryAsync(Guid tenantId, IntegrationDelivery d, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO integration_deliveries (tenant_id, id, integration_id, at_utc, trigger, method, target, status_code, duration_ms, error) " +
            "VALUES (@t, @id, @i, @at, @tr, @m, @tg, @sc, @d, @e)", conn))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("id", d.Id);
            cmd.Parameters.AddWithValue("i", d.IntegrationId);
            cmd.Parameters.AddWithValue("at", d.AtUtc);
            cmd.Parameters.AddWithValue("tr", d.Trigger);
            cmd.Parameters.AddWithValue("m", d.Method);
            cmd.Parameters.AddWithValue("tg", d.Target);
            cmd.Parameters.Add(new NpgsqlParameter("sc", NpgsqlDbType.Integer) { Value = (object?)d.StatusCode ?? DBNull.Value });
            cmd.Parameters.AddWithValue("d", d.DurationMs);
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)d.Error ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var upd = new NpgsqlCommand(
            "UPDATE integrations SET last_used_utc = @at, last_error = @e WHERE tenant_id = @t AND id = @i", conn))
        {
            upd.Parameters.AddWithValue("t", tenantId);
            upd.Parameters.AddWithValue("i", d.IntegrationId);
            upd.Parameters.AddWithValue("at", d.AtUtc);
            upd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)d.Error ?? DBNull.Value });
            await upd.ExecuteNonQueryAsync(ct);
        }

        await using var trim = new NpgsqlCommand(
            "DELETE FROM integration_deliveries WHERE tenant_id = @t AND integration_id = @i AND id NOT IN (" +
            "SELECT id FROM integration_deliveries WHERE tenant_id = @t AND integration_id = @i ORDER BY at_utc DESC LIMIT @k)", conn);
        trim.Parameters.AddWithValue("t", tenantId);
        trim.Parameters.AddWithValue("i", d.IntegrationId);
        trim.Parameters.AddWithValue("k", KeepDeliveries);
        await trim.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IntegrationDelivery>> ListDeliveriesAsync(Guid tenantId, Guid id, int limit, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, integration_id, at_utc, trigger, method, target, status_code, duration_ms, error " +
            "FROM integration_deliveries WHERE tenant_id = @t AND integration_id = @i ORDER BY at_utc DESC LIMIT @l", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("i", id);
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, KeepDeliveries));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<IntegrationDelivery>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new IntegrationDelivery(
                r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8)));
        }

        return list;
    }

    private const string SelectSql =
        "SELECT id, name, kind, enabled, config, secrets, created_utc, last_used_utc, last_error FROM integrations";

    private Integration Read(NpgsqlDataReader r)
    {
        var config = JsonSerializer.Deserialize<IntegrationConfig>(r.GetString(4), Json)
            ?? throw new InvalidOperationException("Integration config is unreadable.");
        var secrets = r.IsDBNull(5) ? new IntegrationSecrets(null, null) : OpenSecrets(r.GetString(5));
        return new Integration(
            r.GetGuid(0), r.GetString(1), Enum.Parse<IntegrationKind>(r.GetString(2)), r.GetBoolean(3), config,
            secrets.Credential is not null, secrets.SigningSecret is not null,
            r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetString(8));
    }

    private string SealSecrets(IntegrationSecrets s) => SecretBox.Seal(JsonSerializer.Serialize(s, Json), _key!);

    /// <summary>A value that cannot be decrypted (key changed or removed) reads as having no secrets, so nothing is sent with them.</summary>
    private IntegrationSecrets OpenSecrets(string stored)
    {
        var plain = SecretBox.Open(stored, _key);
        if (plain is null)
        {
            return new IntegrationSecrets(null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<IntegrationSecrets>(plain, Json) ?? new IntegrationSecrets(null, null);
        }
        catch (JsonException)
        {
            return new IntegrationSecrets(null, null);
        }
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
