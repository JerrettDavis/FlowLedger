using FlowLedger.Application.Features.Categories;
using FlowLedger.SharedKernel;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Per-tenant categorization rules. Created on first use (no EF migration); included in every backup.</summary>
public sealed class NpgsqlCategoryRuleStore(string connectionString, ITenantContext tenant) : ICategoryRuleStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS category_rules (
            tenant_id uuid NOT NULL,
            pattern text NOT NULL,
            category_path text NOT NULL,
            updated_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, pattern)
        );
        """;

    public async Task<IReadOnlyList<CategoryRuleDto>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<CategoryRuleDto>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT pattern, category_path FROM category_rules WHERE tenant_id = @t ORDER BY category_path, pattern", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CategoryRuleDto(r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    public async Task SetAsync(string pattern, string categoryPath, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Serialize writers per tenant so the cap below cannot be raced past; the lock is released at commit.
        await using (var lck = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('category_rules:' || @t::text))", conn, tx))
        {
            lck.Parameters.AddWithValue("t", tenant.TenantId);
            await lck.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO category_rules (tenant_id, pattern, category_path) " +
            "SELECT @t, @p, @c WHERE (SELECT count(*) FROM category_rules WHERE tenant_id = @t) < 1000 " +
            "OR EXISTS (SELECT 1 FROM category_rules WHERE tenant_id = @t AND pattern = @p) " +
            "ON CONFLICT (tenant_id, pattern) DO UPDATE SET category_path = EXCLUDED.category_path, updated_utc = now()", conn, tx);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("p", pattern);
        cmd.Parameters.AddWithValue("c", categoryPath);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> RemoveAsync(string pattern, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM category_rules WHERE tenant_id = @t AND pattern = @p", conn);
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
