using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Budgets;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Per-tenant monthly category budgets through EF (#154). The interface still takes a tenant id; it must be the current tenant,
/// otherwise the call fails loudly instead of silently reading or writing another tenant's rows.
/// </summary>
internal sealed class EfBudgetStore(FlowLedgerDbContext db, ITenantContext tenant) : IBudgetStore
{
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
        return rows.Select(r => new BudgetRow(r.Category, r.MonthlyLimit, r.RowVersion, r.Rollover, r.CarriedAmount, r.CarriedForMonth)).ToList();
    }

    public async Task<uint> SetAsync(Guid tenantId, string category, decimal monthlyLimit, uint? expectedVersion = null, CancellationToken ct = default, bool? rollover = null)
    {
        Guard(tenantId);
        var existing = await db.Budgets.SingleOrDefaultAsync(b => b.Category == category, ct);
        if (existing is null)
        {
            if (expectedVersion is not null)
            {
                throw new ConcurrencyConflictException("This budget was removed elsewhere. Reload it and try again.");
            }

            existing = BudgetRecord.Create(tenantId, category, monthlyLimit, rollover ?? false);
            db.Budgets.Add(existing);
        }
        else
        {
            if (expectedVersion is { } version)
            {
                db.Entry(existing).Property(b => b.RowVersion).OriginalValue = version;
            }

            existing.SetLimit(monthlyLimit);
            if (rollover is { } roll)
            {
                existing.SetRollover(roll);
            }
        }

        await VersionedSave.SaveAsync(db, "budget", ct);
        return existing.RowVersion;
    }

    public async Task<uint> SetCarriedAsync(Guid tenantId, string category, decimal carriedAmount, string month, CancellationToken ct = default)
    {
        Guard(tenantId);
        var existing = await db.Budgets.SingleOrDefaultAsync(b => b.Category == category, ct);
        if (existing is null)
        {
            return 0;
        }

        existing.SetCarried(carriedAmount, month);
        await VersionedSave.SaveAsync(db, "budget", ct);
        return existing.RowVersion;
    }

    public async Task RemoveAsync(Guid tenantId, string category, uint? expectedVersion = null, CancellationToken ct = default)
    {
        Guard(tenantId);
        var existing = await db.Budgets.SingleOrDefaultAsync(b => b.Category == category, ct);
        if (existing is null)
        {
            return; // already gone: deleting twice is not an error
        }

        if (expectedVersion is { } version)
        {
            db.Entry(existing).Property(b => b.RowVersion).OriginalValue = version;
        }

        db.Budgets.Remove(existing);
        await VersionedSave.SaveAsync(db, "budget", ct);
    }
}
