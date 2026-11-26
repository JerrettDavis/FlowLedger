using FlowLedger.Application.Features.Budgets;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Per-tenant monthly category budgets through EF (#154). The interface still takes a tenant id; it must be the current tenant,
/// otherwise the call fails loudly instead of silently reading or writing another tenant's rows.
/// </summary>
internal sealed class EfBudgetStore(FlowLedgerDbContext db, ITenantContext tenant) : IBudgetStore
{
    public const int MaxBudgets = 500;

    private void Guard(Guid tenantId)
    {
        if (tenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("Budgets can only be accessed for the current tenant.");
        }
    }

    public async Task<IReadOnlyList<BudgetRow>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        Guard(tenantId);
        var rows = await db.Budgets.AsNoTracking().OrderBy(b => b.Category).ToListAsync(ct);
        return rows.Select(r => new BudgetRow(r.Category, r.MonthlyLimit)).ToList();
    }

    public async Task SetAsync(Guid tenantId, string category, decimal monthlyLimit, CancellationToken ct = default)
    {
        Guard(tenantId);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO budgets (tenant_id, category, monthly_limit)
               SELECT {tenantId}, {category}, {monthlyLimit}
               WHERE (SELECT count(*) FROM budgets WHERE tenant_id = {tenantId}) < {MaxBudgets}
                  OR EXISTS (SELECT 1 FROM budgets WHERE tenant_id = {tenantId} AND category = {category})
               ON CONFLICT (tenant_id, category) DO UPDATE SET monthly_limit = EXCLUDED.monthly_limit, updated_utc = now()", ct);
        if (affected == 0)
        {
            throw new InvalidOperationException($"You can have at most {MaxBudgets} budgets. Remove one before adding another.");
        }
    }

    public async Task RemoveAsync(Guid tenantId, string category, CancellationToken ct = default)
    {
        Guard(tenantId);
        await db.Budgets.Where(b => b.Category == category).ExecuteDeleteAsync(ct);
    }
}
