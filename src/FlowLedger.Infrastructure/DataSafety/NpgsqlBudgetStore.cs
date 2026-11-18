using FlowLedger.Application.Features.Budgets;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Per-tenant monthly category budgets. Created on first use (IF NOT EXISTS) like the other ad-hoc tables, so it needs no
/// EF migration; it is included in every snapshot/backup/export. Category is the category PATH (e.g. "Food/Groceries").
/// </summary>
public sealed class NpgsqlBudgetStore : IBudgetStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS budgets (
            tenant_id uuid NOT NULL,
            category text NOT NULL,
            monthly_limit numeric(18,2) NOT NULL CHECK (monthly_limit >= 0),
            updated_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, category)
        );
        """;

    private readonly string _connectionString;

    public NpgsqlBudgetStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<BudgetRow>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var list = new List<BudgetRow>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT category, monthly_limit FROM budgets WHERE tenant_id = @t ORDER BY category", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new BudgetRow(r.GetString(0), r.GetDecimal(1)));
        }

        return list;
    }

    public async Task SetAsync(Guid tenantId, string category, decimal monthlyLimit, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO budgets (tenant_id, category, monthly_limit) VALUES (@t, @c, @l) " +
            "ON CONFLICT (tenant_id, category) DO UPDATE SET monthly_limit = EXCLUDED.monthly_limit, updated_utc = now()", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", category);
        cmd.Parameters.AddWithValue("l", monthlyLimit);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(Guid tenantId, string category, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM budgets WHERE tenant_id = @t AND category = @c", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", category);
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
