using FlowLedger.Application.Features.Goals;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Per-tenant savings goals through EF (#154). The interface still takes a tenant id; it must be the current tenant, otherwise
/// the call fails loudly instead of silently reading or writing another tenant's rows.
/// </summary>
internal sealed class EfGoalStore(FlowLedgerDbContext db, ITenantContext tenant) : IGoalStore
{
    public const int MaxGoals = 200;

    private void Guard(Guid tenantId)
    {
        if (tenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("Goals can only be accessed for the current tenant.");
        }
    }

    public async Task<IReadOnlyList<GoalRow>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        Guard(tenantId);
        var rows = await db.Goals.AsNoTracking()
            .OrderBy(g => g.TargetDate == null).ThenBy(g => g.TargetDate).ThenBy(g => g.Name)
            .ToListAsync(ct);
        return rows.Select(r => new GoalRow(r.Id, r.Name, r.TargetAmount, r.SavedAmount, r.TargetDate)).ToList();
    }

    public async Task<Guid> UpsertAsync(Guid tenantId, Guid? id, string name, decimal target, decimal saved, DateOnly? targetDate, CancellationToken ct = default)
    {
        Guard(tenantId);
        var goalId = id ?? Guid.NewGuid();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO savings_goals (tenant_id, id, name, target_amount, saved_amount, target_date)
               SELECT {tenantId}, {goalId}, {name}, {target}, {saved}, {targetDate}
               WHERE (SELECT count(*) FROM savings_goals WHERE tenant_id = {tenantId}) < {MaxGoals}
                  OR EXISTS (SELECT 1 FROM savings_goals WHERE tenant_id = {tenantId} AND id = {goalId})
               ON CONFLICT (tenant_id, id) DO UPDATE SET name = EXCLUDED.name, target_amount = EXCLUDED.target_amount,
               saved_amount = EXCLUDED.saved_amount, target_date = EXCLUDED.target_date, updated_utc = now()", ct);
        if (affected == 0)
        {
            throw new InvalidOperationException($"You can have at most {MaxGoals} savings goals. Remove one before adding another.");
        }

        return goalId;
    }

    public async Task RemoveAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        Guard(tenantId);
        await db.Goals.Where(g => g.Id == id).ExecuteDeleteAsync(ct);
    }
}
