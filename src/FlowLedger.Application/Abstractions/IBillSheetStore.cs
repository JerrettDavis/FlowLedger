using FlowLedger.Application.Features.BillSheet;

namespace FlowLedger.Application.Abstractions;

/// <summary>Tenant-scoped persistence for the bill sheet (rows and monthly paid cells).</summary>
public interface IBillSheetStore
{
    /// <param name="includeInactive">Also returns rows the user deleted (kept so detection does not recreate them).</param>
    Task<IReadOnlyList<SheetBillData>> ListBillsAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<SheetBillData?> GetBillAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts or updates by <see cref="SheetBillData.Id"/>.</summary>
    Task SaveBillAsync(SheetBillData bill, CancellationToken ct = default);

    Task<bool> DeleteBillAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<SheetPaymentData>> ListPaymentsAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken ct = default);

    /// <summary>
    /// Applies upserts (by bill + month + slot) and clears in a single save. A clear with slot -1 removes every slot of that month.
    /// </summary>
    Task ApplyPaymentsAsync(
        IReadOnlyList<SheetPaymentData> upserts,
        IReadOnlyList<(Guid BillId, DateOnly Month, int Slot)> clears,
        CancellationToken ct = default);

    /// <summary>Adds many bills and cells in one save (CSV import).</summary>
    Task AddRangeAsync(IReadOnlyList<SheetBillData> bills, IReadOnlyList<SheetPaymentData> payments, CancellationToken ct = default);

    /// <summary>Removes the bills (and their cells) of the named sections only.</summary>
    Task ClearSectionsAsync(IReadOnlyCollection<string> sections, CancellationToken ct = default);
}

/// <summary>
/// Marks sheet cells paid from synced/imported transactions. Idempotent and safe to call after any
/// sync or import; never throws for "nothing to do".
/// </summary>
public interface ISheetAutoMarker
{
    Task<SheetRefreshResult> RefreshAsync(CancellationToken ct = default);
}
