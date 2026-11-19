using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Per-tenant account to connector links. Created on first use (no EF migration); included in every backup.</summary>
public sealed class NpgsqlAccountConnectorLinkStore(string connectionString, ITenantContext tenant) : IAccountConnectorLinkStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS account_connector_links (
            tenant_id uuid NOT NULL,
            account_id uuid NOT NULL,
            connector_id text NOT NULL,
            provider_account_id text NOT NULL,
            linked_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, account_id)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_account_connector_links_provider
            ON account_connector_links (tenant_id, connector_id, provider_account_id);
        """;

    private const string Columns = "account_id, connector_id, provider_account_id, linked_utc";

    public async Task<IReadOnlyList<AccountConnectorLink>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<AccountConnectorLink>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM account_connector_links WHERE tenant_id = @t ORDER BY connector_id, provider_account_id", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public async Task<AccountConnectorLink?> FindByProviderAccountAsync(string connectorId, string providerAccountId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM account_connector_links WHERE tenant_id = @t AND connector_id = @c AND provider_account_id = @p", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("c", connectorId);
        cmd.Parameters.AddWithValue("p", providerAccountId);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<AccountConnectorLink?> FindByAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM account_connector_links WHERE tenant_id = @t AND account_id = @a", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("a", accountId);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<bool> LinkAsync(Guid accountId, string connectorId, string providerAccountId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO account_connector_links (tenant_id, account_id, connector_id, provider_account_id) VALUES (@t, @a, @c, @p) " +
            "ON CONFLICT (tenant_id, account_id) DO UPDATE SET connector_id = EXCLUDED.connector_id, " +
            "provider_account_id = EXCLUDED.provider_account_id, linked_utc = now()", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("c", connectorId);
        cmd.Parameters.AddWithValue("p", providerAccountId);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false; // that provider account already belongs to another account
        }
    }

    public Task PinManualAsync(Guid accountId, CancellationToken ct = default)
        => LinkAsync(accountId, AccountConnectorLinks.Manual, accountId.ToString("N"), ct);

    public async Task<bool> UnlinkAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM account_connector_links WHERE tenant_id = @t AND account_id = @a", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static AccountConnectorLink Read(NpgsqlDataReader r)
        => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3));

    private static async Task<AccountConnectorLink?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var create = new NpgsqlCommand(CreateSqlText, conn);
        await create.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
