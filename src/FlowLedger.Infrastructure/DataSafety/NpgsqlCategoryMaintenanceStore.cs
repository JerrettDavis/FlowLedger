using FlowLedger.Application.Features.Categories;
using FlowLedger.SharedKernel;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Applies a category move/merge plan in ONE database transaction: category rows, every reference to a merged category
/// (transactions, splits, recurring flows) and the path-keyed side tables (rules, budgets). No new tables, so no migration.
/// </summary>
public sealed class NpgsqlCategoryMaintenanceStore(string connectionString, ITenantContext tenant) : ICategoryMaintenanceStore
{
    public async Task<int> ApplyAsync(CategoryRestructurePlan plan, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        foreach (var create in new[] { NpgsqlBudgetStore.CreateSqlText, NpgsqlCategoryRuleStore.CreateSqlText })
        {
            await using var c = new NpgsqlCommand(create, conn);
            await c.ExecuteNonQueryAsync(ct);
        }

        await using var tx = await conn.BeginTransactionAsync(ct);

        async Task<int> ExecAsync(string sql, params (string Name, object Value)[] args)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (name, value) in args)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            return await cmd.ExecuteNonQueryAsync(ct);
        }

        // 1. moves (rename path / display name / parent in place)
        foreach (var m in plan.Moves)
        {
            await ExecAsync(
                "UPDATE categories SET path = @p, display_name = @d, parent_id = @pid WHERE id = @id AND tenant_id = @t",
                ("p", m.NewPath), ("d", m.DisplayName), ("pid", (object?)m.ParentId ?? DBNull.Value), ("id", m.Id), ("t", tenantId));
        }

        // 2. merges: repoint every reference, then drop the source category
        var affected = 0;
        foreach (var m in plan.Merges)
        {
            affected += await ExecAsync(
                "UPDATE transactions SET category_id = @to WHERE category_id = @from AND tenant_id = @t",
                ("to", m.TargetId), ("from", m.SourceId), ("t", tenantId));
            await ExecAsync(
                "UPDATE transaction_splits SET category_id = @to WHERE category_id = @from",
                ("to", m.TargetId), ("from", m.SourceId));
            await ExecAsync(
                "UPDATE recurring_flows SET category_id = @to WHERE category_id = @from AND tenant_id = @t",
                ("to", m.TargetId), ("from", m.SourceId), ("t", tenantId));
            await ExecAsync(
                "UPDATE categories SET parent_id = NULL WHERE parent_id = @from AND tenant_id = @t",
                ("from", m.SourceId), ("t", tenantId));
            await ExecAsync("DELETE FROM categories WHERE id = @id AND tenant_id = @t", ("id", m.SourceId), ("t", tenantId));
        }

        // 3. path-keyed side tables follow the same old -> new mapping
        var map = plan.Moves.Select(m => (m.OldPath, m.NewPath))
            .Concat(plan.Merges.Select(m => (m.OldPath, NewPath: m.TargetPath)))
            .ToList();
        if (map.Count > 0)
        {
            var olds = map.Select(x => x.OldPath).ToArray();
            var news = map.Select(x => x.NewPath).ToArray();
            await ExecAsync(
                "UPDATE category_rules r SET category_path = m.newp, updated_utc = now() " +
                "FROM unnest(@olds, @news) AS m(oldp, newp) WHERE r.tenant_id = @t AND lower(r.category_path) = lower(m.oldp)",
                ("olds", olds), ("news", news), ("t", tenantId));

            // budgets are keyed by (tenant, category): merging two budgeted categories adds their limits
            var rows = new List<(string Category, decimal Limit)>();
            await using (var read = new NpgsqlCommand("SELECT category, monthly_limit FROM budgets WHERE tenant_id = @t", conn, tx))
            {
                read.Parameters.AddWithValue("t", tenantId);
                await using var r = await read.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    rows.Add((r.GetString(0), r.GetDecimal(1)));
                }
            }

            var lookup = map.GroupBy(x => x.OldPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().NewPath, StringComparer.OrdinalIgnoreCase);
            var moved = rows.Where(x => lookup.ContainsKey(x.Category)).ToList();
            foreach (var row in moved)
            {
                await ExecAsync("DELETE FROM budgets WHERE tenant_id = @t AND category = @c", ("t", tenantId), ("c", row.Category));
            }

            foreach (var group in moved.GroupBy(x => lookup[x.Category], StringComparer.OrdinalIgnoreCase))
            {
                await ExecAsync(
                    "INSERT INTO budgets (tenant_id, category, monthly_limit) VALUES (@t, @c, @l) " +
                    "ON CONFLICT (tenant_id, category) DO UPDATE SET monthly_limit = budgets.monthly_limit + EXCLUDED.monthly_limit, updated_utc = now()",
                    ("t", tenantId), ("c", group.Key), ("l", group.Sum(x => x.Limit)));
            }
        }

        await tx.CommitAsync(ct);
        return affected;
    }
}
