using FlowLedger.Application.Features.Transactions;
using FlowLedger.SharedKernel;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Per-tenant merchant alias rules. Created on first use (IF NOT EXISTS) like the other ad-hoc tables, so it needs no EF
/// migration; backups pick it up because they enumerate every public table.
/// </summary>
public sealed class NpgsqlMerchantAliasStore(string connectionString, ITenantContext tenant) : IMerchantAliasStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS merchant_aliases (
            tenant_id uuid NOT NULL,
            pattern text NOT NULL,
            alias text NOT NULL,
            updated_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, pattern)
        );
        """;

    public async Task<IReadOnlyList<MerchantAliasDto>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<MerchantAliasDto>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT pattern, alias FROM merchant_aliases WHERE tenant_id = @t ORDER BY alias, pattern", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new MerchantAliasDto(r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    public async Task SetAsync(string pattern, string alias, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO merchant_aliases (tenant_id, pattern, alias) VALUES (@t, @p, @a) " +
            "ON CONFLICT (tenant_id, pattern) DO UPDATE SET alias = EXCLUDED.alias, updated_utc = now()", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("p", pattern);
        cmd.Parameters.AddWithValue("a", alias);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RemoveAsync(string pattern, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM merchant_aliases WHERE tenant_id = @t AND pattern = @p", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("p", pattern);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
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
