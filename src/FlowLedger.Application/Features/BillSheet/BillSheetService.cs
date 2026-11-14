using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.BillSheet;

/// <summary>
/// Orchestrates the bill sheet: builds the grid view, edits rows and cells, imports a CSV and
/// (as <see cref="ISheetAutoMarker"/>) marks cells paid from transactions.
/// </summary>
public sealed class BillSheetService(
    IBillSheetStore store,
    ITransactionRepository transactions) : ISheetAutoMarker
{
    public const int DefaultMonthCount = 6;
    private const int TransactionScanLimit = 20_000;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ── View ─────────────────────────────────────────────────────────────────

    public async Task<SheetViewDto> GetViewAsync(string? month, int? monthCount, CancellationToken ct = default)
    {
        var selected = ParseMonth(month) ?? SheetMatcher.FirstOfMonth(Today);
        var count = Math.Clamp(monthCount ?? DefaultMonthCount, 1, 24);
        var months = Enumerable.Range(0, count).Select(i => selected.AddMonths(i - (count - 1))).ToList();

        var bills = await store.ListBillsAsync(ct);
        var payments = await store.ListPaymentsAsync(months[0], months[^1], ct);
        var byBill = payments.GroupBy(p => p.BillId).ToDictionary(g => g.Key, g => g.ToList());

        var sections = bills
            .GroupBy(b => b.Section)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rows = g.OrderBy(b => b.SortOrder).ThenBy(b => b.Name).Select(b => ToRow(b, byBill.GetValueOrDefault(b.Id), months, selected)).ToList();
                return new SheetSectionDto(g.Key, rows, Totals(rows.Select(r => (r, ActualFor(r, selected)))));
            })
            .ToList();

        var allRows = sections.SelectMany(s => s.Rows).Select(r => (r, ActualFor(r, selected))).ToList();
        var paid = allRows.Where(x => x.r.Cells.GetValueOrDefault(Key(selected))?.Paid == true).ToList();
        var summary = new SheetMonthSummaryDto(
            Key(selected), paid.Count, allRows.Count,
            paid.Sum(x => x.Item2),
            allRows.Except(paid).Sum(x => x.r.Estimated));

        return new SheetViewDto(months.Select(Key).ToList(), Key(selected), sections, Totals(allRows), summary);
    }

    private static SheetRowDto ToRow(SheetBillData b, List<SheetPaymentData>? payments, IReadOnlyList<DateOnly> months, DateOnly selected)
    {
        var cells = new Dictionary<string, SheetCellDto>();
        foreach (var p in payments ?? [])
        {
            cells[Key(p.Month)] = new SheetCellDto(p.IsPaid, p.Amount, p.PaidOn, p.Source.ToString());
        }

        return new SheetRowDto(
            b.Id, b.Name, b.Payer, b.IsDebt, b.IsPerpetual, b.IsOptional, b.IsBusiness, b.AutoPay,
            b.DueDay, b.Estimated, b.Actual, b.Service, MaskReference(b.AccountRef), b.MatchText,
            // Nothing has ever ticked this row on its own and it has no explicit match text: tell the user.
            string.IsNullOrWhiteSpace(b.MatchText) && !cells.Values.Any(c => c.Source == nameof(SheetPaymentSource.Auto)),
            cells);
    }

    /// <summary>The "Actual Cost" column: this month's payment if paid, else the last known actual, else the estimate.</summary>
    private static decimal ActualFor(SheetRowDto r, DateOnly month)
        => r.Cells.GetValueOrDefault(Key(month)) is { Paid: true, Amount: { } a } ? a : r.Actual ?? r.Estimated;

    private static SheetTotalsDto Totals(IEnumerable<(SheetRowDto Row, decimal Actual)> rows)
    {
        var list = rows.ToList();
        return new SheetTotalsDto(
            Line(list.Select(x => (x.Row, x.Row.Estimated))),
            Line(list.Select(x => (x.Row, x.Actual))));
    }

    private static SheetTotalsLine Line(IEnumerable<(SheetRowDto Row, decimal Amount)> rows)
    {
        var list = rows.ToList();
        return new SheetTotalsLine(
            list.Sum(x => x.Amount),
            list.Where(x => x.Row.IsOptional).Sum(x => x.Amount),
            list.Where(x => !x.Row.IsOptional).Sum(x => x.Amount),
            list.Where(x => !x.Row.IsBusiness).Sum(x => x.Amount),
            list.Where(x => x.Row.IsBusiness).Sum(x => x.Amount));
    }

    /// <summary>Shows only the last four digits of anything that looks like an account number.</summary>
    public static string? MaskReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length >= 6 ? "••••" + digits[^4..] : value;
    }

    // ── Rows ─────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateBillAsync(SaveBillRequest r, CancellationToken ct = default)
    {
        Validate(r);
        var existing = await store.ListBillsAsync(ct);
        var section = r.Section.Trim();
        var next = existing.Where(b => b.Section == section).Select(b => b.SortOrder + 1).DefaultIfEmpty(0).Max();
        var bill = ToData(Guid.NewGuid(), next, r, null);
        await store.SaveBillAsync(bill, ct);
        return bill.Id;
    }

    public async Task<bool> UpdateBillAsync(Guid id, SaveBillRequest r, CancellationToken ct = default)
    {
        Validate(r);
        var current = await store.GetBillAsync(id, ct);
        if (current is null)
        {
            return false;
        }

        // A null/masked AccountRef from the client means "unchanged".
        var accountRef = string.IsNullOrWhiteSpace(r.AccountRef) || r.AccountRef == MaskReference(current.AccountRef)
            ? current.AccountRef
            : r.AccountRef.Trim();
        await store.SaveBillAsync(ToData(id, current.SortOrder, r, accountRef), ct);
        return true;
    }

    public Task<bool> DeleteBillAsync(Guid id, CancellationToken ct = default) => store.DeleteBillAsync(id, ct);

    private static SheetBillData ToData(Guid id, int order, SaveBillRequest r, string? accountRef)
        => new(
            id, r.Section.Trim(), order, r.Name.Trim(), r.Payer?.Trim(), r.IsDebt, r.IsPerpetual, r.IsOptional,
            r.IsBusiness, r.AutoPay, r.DueDay, r.Estimated, r.Actual, r.Service?.Trim(),
            accountRef ?? (string.IsNullOrWhiteSpace(r.AccountRef) ? null : r.AccountRef.Trim()),
            string.IsNullOrWhiteSpace(r.MatchText) ? null : r.MatchText.Trim(), true);

    private static void Validate(SaveBillRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200)
        {
            throw new SheetOperationException("Name is required (max 200 characters).");
        }

        if (string.IsNullOrWhiteSpace(r.Section) || r.Section.Length > 100)
        {
            throw new SheetOperationException("Section is required (max 100 characters).");
        }

        if (r.DueDay is < 1 or > 31)
        {
            throw new SheetOperationException("Due day must be between 1 and 31, or empty for a floating date.");
        }

        if (r.Estimated < 0 || r.Actual < 0)
        {
            throw new SheetOperationException("Amounts cannot be negative.");
        }
    }

    // ── Cells ────────────────────────────────────────────────────────────────

    /// <summary>Removes any manual or automatic mark so the cell returns to automatic.</summary>
    public async Task<bool> ClearPaymentAsync(Guid billId, string month, CancellationToken ct = default)
    {
        var m = ParseMonth(month) ?? throw new SheetOperationException("Month must look like 2026-11.");
        if (await store.GetBillAsync(billId, ct) is null)
        {
            return false;
        }

        await store.ApplyPaymentsAsync([], [(billId, m)], ct);
        return true;
    }

    public async Task<bool> SetPaymentAsync(Guid billId, string month, SetPaymentRequest r, CancellationToken ct = default)
    {
        var m = ParseMonth(month) ?? throw new SheetOperationException("Month must look like 2026-11.");
        var bill = await store.GetBillAsync(billId, ct);
        if (bill is null)
        {
            return false;
        }

        decimal? amount = r.Paid ? r.Amount ?? bill.Actual ?? bill.Estimated : null;
        var payment = new SheetPaymentData(billId, m, r.Paid, amount, r.Paid ? Today : null, null, SheetPaymentSource.Manual);
        await store.ApplyPaymentsAsync([payment], [], ct);
        return true;
    }

    // ── Auto-marking ─────────────────────────────────────────────────────────

    public async Task<SheetRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var bills = await store.ListBillsAsync(ct);
        if (bills.Count == 0)
        {
            return new SheetRefreshResult(0, 0);
        }

        var current = SheetMatcher.FirstOfMonth(Today);
        var months = Enumerable.Range(0, DefaultMonthCount).Select(i => current.AddMonths(-i)).ToList();
        var from = months[^1].AddDays(-SheetMatcher.MaxDueDayDistance);
        var to = current.AddMonths(1).AddDays(SheetMatcher.MaxDueDayDistance);

        var existing = await store.ListPaymentsAsync(months[^1], current, ct);
        var all = await transactions.ListAllAsync(TransactionScanLimit, ct);
        var txns = all
            .Where(t => t.Direction == TransactionDirection.Debit
                && t.Status is TransactionStatus.Pending or TransactionStatus.Posted or TransactionStatus.Matched
                    or TransactionStatus.Reconciled or TransactionStatus.NeedsReview
                && t.EffectiveDate >= from && t.EffectiveDate <= to)
            .Select(t => new SheetTxn(t.Id, t.EffectiveDate, t.Amount.Amount, $"{t.MerchantName} {t.Description}"))
            .ToList();

        var result = SheetMatcher.Match(bills, existing, txns, months);
        if (result.Upserts.Count > 0 || result.Clears.Count > 0)
        {
            await store.ApplyPaymentsAsync(result.Upserts, result.Clears, ct);
        }

        return new SheetRefreshResult(result.Upserts.Count, result.Clears.Count);
    }

    // ── Import ───────────────────────────────────────────────────────────────

    public async Task<SheetImportResult> ImportCsvAsync(ImportSheetRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Csv))
        {
            throw new SheetOperationException("CSV text is empty.");
        }

        var parsed = SheetCsvImporter.Parse(request.Csv, Today);
        if (parsed.Bills.Count == 0)
        {
            throw new SheetOperationException("No bill rows found. The CSV needs a header row containing \"Estimated Cost\".");
        }

        // Each tab is its own section, so importing the Credit Cards tab never touches the bills already loaded.
        var incoming = parsed.Bills.Select(b => b.Section).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = (await store.ListBillsAsync(ct)).Where(b => incoming.Contains(b.Section)).ToList();
        if (existing.Count > 0)
        {
            if (!request.Replace)
            {
                throw new SheetOperationException(
                    $"The sheet already has {existing.Count} rows in {string.Join(", ", existing.Select(b => b.Section).Distinct())}. " +
                    "Import again with replace enabled to overwrite just those sections.");
            }

            await store.ClearSectionsAsync(existing.Select(b => b.Section).Distinct().ToList(), ct);
        }

        await store.AddRangeAsync(parsed.Bills, parsed.Payments, ct);
        return new SheetImportResult(parsed.Sections, parsed.Bills.Count, parsed.Payments.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static string Key(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static DateOnly? ParseMonth(string? s)
        => DateOnly.TryParseExact(s is null ? null : s.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
