using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Goals;
using FlowLedger.Infrastructure.Persistence.Entities;
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
        return rows.Select(r => new GoalRow(r.Id, r.Name, r.TargetAmount, r.SavedAmount, r.TargetDate, r.RowVersion)).ToList();
    }

    public async Task<(Guid Id, uint Version)> UpsertAsync(
        Guid tenantId, Guid? id, string name, decimal target, decimal saved, DateOnly? targetDate, uint? expectedVersion = null, CancellationToken ct = default)
    {
        Guard(tenantId);
        var goalId = id ?? Guid.NewGuid();
        var existing = id is null ? null : await db.Goals.SingleOrDefaultAsync(g => g.Id == goalId, ct);
        if (existing is null)
        {
            if (expectedVersion is not null)
            {
                throw new ConcurrencyConflictException("This goal was removed elsewhere. Reload it and try again.");
            }

            if (await db.Goals.CountAsync(ct) >= MaxGoals)
            {
                throw new InvalidOperationException($"You can have at most {MaxGoals} savings goals. Remove one before adding another.");
            }

            existing = GoalRecord.Create(tenantId, goalId, name, target, saved, targetDate);
            db.Goals.Add(existing);
        }
        else
        {
            if (expectedVersion is { } version)
            {
                db.Entry(existing).Property(g => g.RowVersion).OriginalValue = version;
            }

            existing.Update(name, target, saved, targetDate);
        }

        await VersionedSave.SaveAsync(db, "goal", ct);
        return (goalId, existing.RowVersion);
    }

    public async Task RemoveAsync(Guid tenantId, Guid id, uint? expectedVersion = null, CancellationToken ct = default)
    {
        Guard(tenantId);
        var existing = await db.Goals.SingleOrDefaultAsync(g => g.Id == id, ct);
        if (existing is null)
        {
            return; // already gone: deleting twice is not an error
        }

        if (expectedVersion is { } version)
        {
            db.Entry(existing).Property(g => g.RowVersion).OriginalValue = version;
        }

        db.Goals.Remove(existing);
        await VersionedSave.SaveAsync(db, "goal", ct);
    }
}
