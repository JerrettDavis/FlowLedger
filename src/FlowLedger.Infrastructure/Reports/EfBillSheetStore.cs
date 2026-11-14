using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.BillSheet;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Reports;

internal sealed class EfBillSheetStore(FlowLedgerDbContext db, ITenantContext tenant) : IBillSheetStore
{
    public async Task<IReadOnlyList<SheetBillData>> ListBillsAsync(bool includeInactive = false, CancellationToken ct = default)
        => (await db.SheetBills.AsNoTracking().Where(b => includeInactive || b.IsActive)
            .OrderBy(b => b.Section).ThenBy(b => b.SortOrder).ThenBy(b => b.Name).ToListAsync(ct))
            .Select(ToData).ToList();

    public async Task<SheetBillData?> GetBillAsync(Guid id, CancellationToken ct = default)
    {
        var r = await db.SheetBills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.IsActive, ct);
        return r is null ? null : ToData(r);
    }

    public async Task SaveBillAsync(SheetBillData bill, CancellationToken ct = default)
    {
        var record = await db.SheetBills.FirstOrDefaultAsync(b => b.Id == bill.Id, ct);
        if (record is null)
        {
            record = new SheetBillRecord { Id = bill.Id, TenantId = tenant.TenantId, CreatedAt = DateTimeOffset.UtcNow };
            await db.SheetBills.AddAsync(record, ct);
            Apply(record, bill);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (bill.Section == "Detected")
            {
                // A concurrent refresh already created this detected row; drop ours and carry on.
                db.Entry(record).State = EntityState.Detached;
            }

            return;
        }

        Apply(record, bill);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteBillAsync(Guid id, CancellationToken ct = default)
    {
        var record = await db.SheetBills.FirstOrDefaultAsync(b => b.Id == id && b.IsActive, ct);
        if (record is null)
        {
            return false;
        }

        // Soft delete: the row stays (inactive) so auto-detection does not bring it back after a sync.
        record.IsActive = false;
        db.SheetPayments.RemoveRange(await db.SheetPayments.Where(p => p.BillId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<SheetPaymentData>> ListPaymentsAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken ct = default)
        => (await db.SheetPayments.AsNoTracking().Where(p => p.Month >= fromMonth && p.Month <= toMonth).ToListAsync(ct))
            .Select(ToData).ToList();

    public async Task ApplyPaymentsAsync(
        IReadOnlyList<SheetPaymentData> upserts,
        IReadOnlyList<(Guid BillId, DateOnly Month)> clears,
        CancellationToken ct = default)
    {
        var touched = upserts.Select(u => (u.BillId, u.Month)).Concat(clears).ToHashSet();
        var billIds = touched.Select(t => t.Item1).Distinct().ToList();
        var months = touched.Select(t => t.Item2).Distinct().ToList();
        var current = (await db.SheetPayments.Where(p => billIds.Contains(p.BillId) && months.Contains(p.Month)).ToListAsync(ct))
            .ToDictionary(p => (p.BillId, p.Month));

        // Clear first and save so a freed transaction can be re-assigned without tripping the unique index.
        foreach (var key in clears)
        {
            if (current.Remove(key, out var gone))
            {
                db.SheetPayments.Remove(gone);
            }
        }

        if (clears.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        foreach (var u in upserts)
        {
            if (current.TryGetValue((u.BillId, u.Month), out var row))
            {
                Apply(row, u);
            }
            else
            {
                var added = new SheetPaymentRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId };
                Apply(added, u);
                await db.SheetPayments.AddAsync(added, ct);
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (upserts.Any(u => u.Source == SheetPaymentSource.Auto))
        {
            // A concurrent refresh claimed the same cell/transaction first; the next run reconciles.
            throw new SheetOperationException("The sheet changed while refreshing; please retry. " + ex.GetType().Name);
        }
    }

    public async Task AddRangeAsync(IReadOnlyList<SheetBillData> bills, IReadOnlyList<SheetPaymentData> payments, CancellationToken ct = default)
    {
        foreach (var b in bills)
        {
            var r = new SheetBillRecord { Id = b.Id, TenantId = tenant.TenantId, CreatedAt = DateTimeOffset.UtcNow };
            Apply(r, b);
            await db.SheetBills.AddAsync(r, ct);
        }

        foreach (var p in payments)
        {
            var r = new SheetPaymentRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId };
            Apply(r, p);
            await db.SheetPayments.AddAsync(r, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ClearSectionsAsync(IReadOnlyCollection<string> sections, CancellationToken ct = default)
    {
        var bills = await db.SheetBills.Where(b => sections.Contains(b.Section)).ToListAsync(ct);
        var ids = bills.Select(b => b.Id).ToList();
        db.SheetPayments.RemoveRange(await db.SheetPayments.Where(p => ids.Contains(p.BillId)).ToListAsync(ct));
        db.SheetBills.RemoveRange(bills);
        await db.SaveChangesAsync(ct);
    }

    private static SheetBillData ToData(SheetBillRecord r)
        => new(r.Id, r.Section, r.SortOrder, r.Name, r.Payer, r.IsDebt, r.IsPerpetual, r.IsOptional, r.IsBusiness,
            r.AutoPay, r.DueDay, r.Estimated, r.Actual, r.Service, r.AccountRef, r.MatchText, r.IsActive);

    private static void Apply(SheetBillRecord r, SheetBillData d)
    {
        r.Section = d.Section;
        r.SortOrder = d.SortOrder;
        r.Name = d.Name;
        r.Payer = d.Payer;
        r.IsDebt = d.IsDebt;
        r.IsPerpetual = d.IsPerpetual;
        r.IsOptional = d.IsOptional;
        r.IsBusiness = d.IsBusiness;
        r.AutoPay = d.AutoPay;
        r.DueDay = d.DueDay;
        r.Estimated = d.Estimated;
        r.Actual = d.Actual;
        r.Service = d.Service;
        r.AccountRef = d.AccountRef;
        r.MatchText = d.MatchText;
        r.IsActive = d.IsActive;
    }

    private static SheetPaymentData ToData(SheetPaymentRecord r)
        => new(r.BillId, r.Month, r.IsPaid, r.Amount, r.PaidOn, r.TransactionId,
            Enum.TryParse<SheetPaymentSource>(r.Source, out var s) ? s : SheetPaymentSource.Manual);

    private static void Apply(SheetPaymentRecord r, SheetPaymentData d)
    {
        r.BillId = d.BillId;
        r.Month = d.Month;
        r.IsPaid = d.IsPaid;
        r.Amount = d.Amount;
        r.PaidOn = d.PaidOn;
        r.TransactionId = d.TransactionId;
        r.Source = d.Source.ToString();
    }
}
