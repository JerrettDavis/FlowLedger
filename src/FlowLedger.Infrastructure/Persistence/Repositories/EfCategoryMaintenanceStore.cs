using FlowLedger.Application.Features.Categories;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Applies a category move/merge plan in ONE database transaction through the tenant-scoped EF context (#154): category rows, every
/// reference to a merged category (transactions, splits, recurring flows) and the path-keyed side tables (rules, budgets). The
/// set-based statements are raw SQL on EF's own connection and transaction, and every one of them is keyed by the current tenant.
/// No new tables, so no migration.
/// </summary>
internal sealed class EfCategoryMaintenanceStore(FlowLedgerDbContext db, ITenantContext tenant) : ICategoryMaintenanceStore
{
    public async Task<int> ApplyAsync(CategoryRestructurePlan plan, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;

        // The production context retries transient failures, which forbids a bare user transaction: run the whole unit in the strategy.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var affected = 0;

            // 1. moves (rename path / display name / parent in place)
            foreach (var m in plan.Moves)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE categories SET path = {m.NewPath}, display_name = {m.DisplayName}, parent_id = {m.ParentId} WHERE id = {m.Id} AND tenant_id = {tenantId}", ct);
            }

            // 2. merges: repoint every reference, then drop the source category
            foreach (var m in plan.Merges)
            {
                affected += await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE transactions SET category_id = {m.TargetId} WHERE category_id = {m.SourceId} AND tenant_id = {tenantId}", ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE transaction_splits s SET category_id = {m.TargetId} FROM transactions t
                       WHERE s.transaction_id = t.id AND t.tenant_id = {tenantId} AND s.category_id = {m.SourceId}", ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE recurring_flows SET category_id = {m.TargetId} WHERE category_id = {m.SourceId} AND tenant_id = {tenantId}", ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE categories SET parent_id = NULL WHERE parent_id = {m.SourceId} AND tenant_id = {tenantId}", ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM categories WHERE id = {m.SourceId} AND tenant_id = {tenantId}", ct);
            }

            // 3. path-keyed side tables follow the same old -> new mapping
            var map = plan.Moves.Select(m => (m.OldPath, m.NewPath))
                .Concat(plan.Merges.Select(m => (m.OldPath, NewPath: m.TargetPath)))
                .ToList();
            if (map.Count > 0)
            {
                var olds = map.Select(x => x.OldPath).ToArray();
                var news = map.Select(x => x.NewPath).ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE category_rules r SET category_path = m.newp, updated_utc = now()
                       FROM unnest({olds}, {news}) AS m(oldp, newp) WHERE r.tenant_id = {tenantId} AND lower(r.category_path) = lower(m.oldp)", ct);

                // budgets are keyed by (tenant, category): merging two budgeted categories adds their limits (the query filter scopes the tenant)
                var rows = await db.Budgets.AsNoTracking().Select(b => new { b.Category, b.MonthlyLimit }).ToListAsync(ct);
                var lookup = map.GroupBy(x => x.OldPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().NewPath, StringComparer.OrdinalIgnoreCase);
                var moved = rows.Where(x => lookup.ContainsKey(x.Category)).ToList();
                foreach (var row in moved)
                {
                    var category = row.Category;
                    await db.Budgets.Where(b => b.Category == category).ExecuteDeleteAsync(ct);
                }

                foreach (var group in moved.GroupBy(x => lookup[x.Category], StringComparer.OrdinalIgnoreCase))
                {
                    var target = group.Key;
                    var limit = group.Sum(x => x.MonthlyLimit);
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $@"INSERT INTO budgets (tenant_id, category, monthly_limit) VALUES ({tenantId}, {target}, {limit})
                           ON CONFLICT (tenant_id, category) DO UPDATE SET monthly_limit = budgets.monthly_limit + EXCLUDED.monthly_limit, updated_utc = now()", ct);
                }
            }

            await tx.CommitAsync(ct);
            return affected;
        });
    }
}
