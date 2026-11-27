using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.BillSheet;

/// <summary>
/// Orchestrates the bill sheet: builds the grid view, edits rows and cells, imports a CSV and
/// (as <see cref="ISheetAutoMarker"/>) marks cells paid from transactions.
/// </summary>
public sealed class BillSheetService(
    IBillSheetStore store,
    ITransactionRepository transactions,
    IAccountRepository? accounts = null,
    ICategoryRepository? categories = null,
    TimeProvider? clock = null) : ISheetAutoMarker
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public const int DefaultMonthCount = 6;
    public const string DetectedSection = "Detected";
    private const int TransactionScanLimit = 20_000;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    // ── View ─────────────────────────────────────────────────────────────────

    public async Task<SheetViewDto> GetViewAsync(string? month, int? monthCount, int? monthsAhead = null, CancellationToken ct = default)
    {
        var selected = ParseMonth(month) ?? SheetMatcher.FirstOfMonth(Today);
        var count = Math.Clamp(monthCount ?? DefaultMonthCount, 1, 24);
        // The window ends `ahead` months after the selected month (default 0), so the UI can show last / current / next.
        var ahead = Math.Clamp(monthsAhead ?? 0, 0, count - 1);
        var months = Enumerable.Range(0, count).Select(i => selected.AddMonths(i - (count - 1 - ahead))).ToList();

        var bills = await store.ListBillsAsync(ct: ct);
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

        // Progress counts every payment due this month: a fortnightly bill counts two or three times.
        var slots = allRows.SelectMany(x => SlotsFor(x.r, selected).Select(s => (Row: x.r, Slot: s))).ToList();
        var paidSlots = slots.Where(x => x.Slot.Paid).ToList();
        var summary = new SheetMonthSummaryDto(
            Key(selected), paidSlots.Count, slots.Count,
            paidSlots.Sum(x => x.Slot.Amount ?? x.Row.Actual ?? x.Row.Estimated),
            slots.Where(x => !x.Slot.Paid).Sum(x => x.Row.Estimated));

        return new SheetViewDto(months.Select(Key).ToList(), Key(selected), sections, Totals(allRows), summary);
    }

    private static SheetRowDto ToRow(SheetBillData b, List<SheetPaymentData>? payments, IReadOnlyList<DateOnly> months, DateOnly selected)
    {
        var cells = new Dictionary<string, SheetCellDto>();
        var slots = new Dictionary<string, IReadOnlyList<SheetSlotDto>>();
        foreach (var month in months)
        {
            var due = BillSchedule.Slots(b, month);
            var monthPayments = (payments ?? []).Where(p => p.Month == month).ToList();
            var list = due.Select((d, i) =>
            {
                var p = monthPayments.FirstOrDefault(x => x.Slot == i);
                return new SheetSlotDto(i, d, p?.IsPaid == true, p?.Amount, p?.PaidOn, p?.Source.ToString());
            }).ToList();
            slots[Key(month)] = list;

            if (monthPayments.Count == 0)
            {
                continue;
            }

            // Collapse the month's slots into one cell: paid only when every expected payment is.
            var paidSlots = monthPayments.Where(p => p.IsPaid && p.Slot < due.Count).ToList();
            var allPaid = paidSlots.Count >= due.Count;
            var lastPaid = paidSlots.Select(p => p.PaidOn).Where(d => d is not null).DefaultIfEmpty(null).Max();
            var source = monthPayments.All(p => p.Source == SheetPaymentSource.Auto) ? nameof(SheetPaymentSource.Auto) : nameof(SheetPaymentSource.Manual);
            cells[Key(month)] = due.Count <= 1
                ? new SheetCellDto(monthPayments[0].IsPaid, monthPayments[0].Amount, monthPayments[0].PaidOn, monthPayments[0].Source.ToString())
                : new SheetCellDto(allPaid, paidSlots.Count == 0 ? null : paidSlots.Sum(p => p.Amount ?? 0m), lastPaid, source, paidSlots.Count, due.Count);
        }

        var selectedSlots = BillSchedule.Slots(b, selected).Count;
        return new SheetRowDto(
            b.Id, b.Name, b.Payer, b.IsDebt, b.IsPerpetual, b.IsOptional, b.IsBusiness, b.AutoPay,
            b.DueDay, b.Estimated, b.Actual, b.Service, MaskReference(b.AccountRef), b.MatchText,
            // Nothing has ever ticked this row on its own and it has no explicit match text: tell the user.
            string.IsNullOrWhiteSpace(b.MatchText) && !cells.Values.Any(c => c.Source == nameof(SheetPaymentSource.Auto)),
            cells, b.Frequency, b.IntervalWeeks, b.AnchorDate, b.SecondDueDay, slots,
            b.Estimated * Math.Max(1, selectedSlots));
    }

    /// <summary>The "Actual Cost" column: this month's payment if paid, else the last known actual, else the estimate.</summary>
    private static decimal ActualFor(SheetRowDto r, DateOnly month)
    {
        var slots = SlotsFor(r, month);
        if (slots.Count <= 1)
        {
            return r.Cells.GetValueOrDefault(Key(month)) is { Paid: true, Amount: { } a } ? a : r.Actual ?? r.Estimated;
        }

        // Several payments in the month: what was actually paid, plus the expected amount for each one still due.
        return slots.Sum(s => s.Paid && s.Amount is { } a ? a : r.Actual ?? r.Estimated);
    }

    private static IReadOnlyList<SheetSlotDto> SlotsFor(SheetRowDto r, DateOnly month)
        => r.Slots?.GetValueOrDefault(Key(month)) ?? [new SheetSlotDto(0, null, r.Cells.GetValueOrDefault(Key(month))?.Paid == true,
            r.Cells.GetValueOrDefault(Key(month))?.Amount, null, null)];

    private static SheetTotalsDto Totals(IEnumerable<(SheetRowDto Row, decimal Actual)> rows)
    {
        var list = rows.ToList();
        return new SheetTotalsDto(
            Line(list.Select(x => (x.Row, x.Row.MonthlyEstimated))),
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
        var existing = await store.ListBillsAsync(ct: ct);
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
            string.IsNullOrWhiteSpace(r.MatchText) ? null : r.MatchText.Trim(), true,
            string.IsNullOrWhiteSpace(r.Frequency) ? BillFrequency.Monthly : r.Frequency,
            r.IntervalWeeks, r.AnchorDate, r.SecondDueDay);

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

        var frequency = string.IsNullOrWhiteSpace(r.Frequency) ? BillFrequency.Monthly : r.Frequency;
        if (!BillFrequency.IsKnown(frequency))
        {
            throw new SheetOperationException("Frequency must be Monthly, TwiceMonthly, Weekly or EveryNWeeks.");
        }

        if (frequency is BillFrequency.Weekly or BillFrequency.EveryNWeeks && r.AnchorDate is null)
        {
            throw new SheetOperationException("Weekly and every-N-weeks rows need a date one payment falls on (the anchor date).");
        }

        if (frequency == BillFrequency.EveryNWeeks && r.IntervalWeeks is null or < 2 or > 52)
        {
            throw new SheetOperationException("Interval must be between 2 and 52 weeks.");
        }

        if (frequency == BillFrequency.TwiceMonthly
            && (r.DueDay is not { } first || r.SecondDueDay is not { } second || second is < 1 or > 31 || first >= second))
        {
            throw new SheetOperationException("Twice-monthly rows need two due days, the first earlier than the second.");
        }
    }

    // ── Cells ────────────────────────────────────────────────────────────────

    /// <summary>Removes any manual or automatic mark so the cell returns to automatic.</summary>
    public async Task<bool> ClearPaymentAsync(Guid billId, string month, int? slot = null, CancellationToken ct = default)
    {
        var m = ParseMonth(month) ?? throw new SheetOperationException("Month must look like 2026-11.");
        if (await store.GetBillAsync(billId, ct) is null)
        {
            return false;
        }

        await store.ApplyPaymentsAsync([], [(billId, m, slot ?? -1)], ct);
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
        if (r.Slot < 0 || r.Slot >= Math.Max(1, BillSchedule.Slots(bill, m).Count))
        {
            throw new SheetOperationException("That payment does not exist in this month.");
        }

        var payment = new SheetPaymentData(billId, m, r.Paid, amount, r.Paid ? Today : null, null, SheetPaymentSource.Manual, r.Slot);
        await store.ApplyPaymentsAsync([payment], [], ct);
        return true;
    }

    // ── Auto-marking ─────────────────────────────────────────────────────────

    public async Task<SheetRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        var current = SheetMatcher.FirstOfMonth(Today);
        var months = Enumerable.Range(0, DefaultMonthCount).Select(i => current.AddMonths(-i)).ToList();
        var from = months[^1].AddDays(-SheetMatcher.MaxDueDayDistance);
        var to = current.AddMonths(1).AddDays(SheetMatcher.MaxDueDayDistance);

        var existing = await store.ListPaymentsAsync(months[^1], current, ct);
        var all = await transactions.ListAsync(null, from, to, 0, TransactionScanLimit, ct);

        // Investments are assets, not bills: leave out transactions on investment/holding accounts and anything categorised
        // as an investment contribution, trade or dividend, so they never become (or tick) sheet rows.
        var investmentAccounts = accounts is null ? [] : (await accounts.ListIncludingInactiveAsync(ct))
            .Where(Investments.InvestmentExclusion.IsInvestmentAccount).Select(a => a.Id).ToHashSet();
        var investmentCategories = categories is null ? [] : (await categories.ListAsync(ct))
            .Where(c => Investments.InvestmentExclusion.IsInvestmentCategory(c.Path.Value)).Select(c => c.Id).ToHashSet();
        bool IsInvestment(Transaction t) => investmentAccounts.Contains(t.AccountId.Value) || (t.CategoryId is { } c && investmentCategories.Contains(c.Value));

        var investmentTxns = all.Where(IsInvestment)
            .Select(t => new SheetTxn(t.Id, t.EffectiveDate, t.Amount.Amount, $"{t.MerchantName} {t.Description}", t.MerchantName ?? t.Description))
            .ToList();
        all = all.Where(t => !IsInvestment(t)).ToList();

        // Transfers (#68): a card/loan payment shows up as two legs. The paying leg may tick a bill row; the receiving leg on the
        // liability account is dropped so it can neither pay the row twice nor be detected as a new recurring expense.
        if (accounts is not null)
        {
            var types = (await accounts.ListIncludingInactiveAsync(ct)).ToDictionary(a => a.Id, a => a.AccountType.ToString());
            var dtos = all.Select(TransactionMapper.ToDto).ToList();
            var receiving = TransferDetector.LiabilitySideLegs(dtos, TransferDetector.Detect(dtos, types), types);
            all = all.Where(t => !receiving.Contains(t.Id)).ToList();
        }

        var txns = all
            .Where(t => t.Direction == TransactionDirection.Debit
                && t.Status is TransactionStatus.Pending or TransactionStatus.Posted or TransactionStatus.Matched
                    or TransactionStatus.Reconciled or TransactionStatus.NeedsReview
                && t.EffectiveDate >= from && t.EffectiveDate <= to)
            .Select(t => new SheetTxn(t.Id, t.EffectiveDate, t.Amount.Amount, $"{t.MerchantName} {t.Description}", t.MerchantName ?? t.Description))
            .ToList();

        // Auto-build: add a row for every monthly-repeating expense that no existing (or deleted) row covers.
        var known = await store.ListBillsAsync(includeInactive: true, ct);
        var added = 0;

        // A row covers a detected merchant if it matches the merchant's label OR the bank text of any of its transactions:
        // once an alias renames "AMER ELECT PWR CPPWDRAWAL" to "AEP", the old row still matches the raw text, and the
        // alias label alone would add a duplicate.
        bool IsCovered(IReadOnlyList<SheetBillData> rows, string name)
        {
            if (rows.Any(b => SheetMatcher.BillMatchesText(b, name)))
            {
                return true;
            }

            var key = RecurringDetector.Key(name);
            var normKey = MerchantNormalizer.Key(name);
            return txns.Any(t =>
            {
                var label = t.Label ?? t.Text;
                return (RecurringDetector.Key(label) == key || MerchantNormalizer.Key(label) == normKey)
                    && rows.Any(b => SheetMatcher.BillMatchesText(b, t.Text));
            });
        }

        // Merchants that charge weekly, every other week or twice a month (a $55 mow every second Tuesday) are not
        // monthly bills: detect them first so they get a row that repeats, instead of a single monthly $55.
        var repeating = RecurringFlowDetector
            .Detect(txns.Select(t => new FlowTxn(Guid.Empty, t.Date, t.Amount, false, t.Label ?? t.Text)).ToList(), Today)
            .Where(f => f.Frequency is BillFrequency.Weekly or BillFrequency.EveryNWeeks or BillFrequency.TwiceMonthly
                && f.AmountModel == "Fixed" && f.Confidence >= 0.8)
            .ToList();
        foreach (var f in repeating)
        {
            var covering = known.FirstOrDefault(b => SheetMatcher.BillMatchesText(b, f.Name))
                ?? known.FirstOrDefault(b => IsCovered([b], f.Name));
            if (covering is null)
            {
                var order = known.Where(b => b.Section == DetectedSection).Select(b => b.SortOrder + 1).DefaultIfEmpty(0).Max();
                var bill = RepeatingBill(Guid.NewGuid(), DetectedSection, order, f, "Detected from your transactions");
                await store.SaveBillAsync(bill, ct);
                known = [.. known, bill];
                added++;
            }
            else if (covering is { Section: DetectedSection, Frequency: BillFrequency.Monthly, IsActive: true })
            {
                // An earlier refresh saw this as a monthly bill; now there is enough history to know it repeats more often.
                var fixedUp = RepeatingBill(covering.Id, covering.Section, covering.SortOrder, f, covering.Service) with
                {
                    Name = covering.Name,
                    MatchText = covering.MatchText,
                };
                await store.SaveBillAsync(fixedUp, ct);
                known = known.Select(b => b.Id == covering.Id ? fixedUp : b).ToList();
            }
        }

        var repeatingKeys = repeating.Select(f => RecurringDetector.Key(f.Name)).ToHashSet();
        foreach (var d in RecurringDetector.Detect(txns))
        {
            if (repeatingKeys.Contains(RecurringDetector.Key(d.Name)) || IsCovered(known, d.Name))
            {
                continue;
            }

            var order = known.Where(b => b.Section == DetectedSection).Select(b => b.SortOrder + 1).DefaultIfEmpty(0).Max();
            var bill = new SheetBillData(
                Guid.NewGuid(), DetectedSection, order, d.Name, null, false, true, false, false, false,
                d.DueDay, d.Estimated, d.Actual, "Detected from your transactions", null, d.MatchText, true);
            await store.SaveBillAsync(bill, ct);
            known = [.. known, bill];
            added++;
        }

        // Rows an earlier refresh auto-detected from investment activity are retired (rows the user built are left alone).
        foreach (var stale in known.Where(b => b.IsActive && b.Section == DetectedSection
                     && investmentTxns.Any(t => SheetMatcher.BillMatchesText(b, t.Text))
                     && !txns.Any(t => SheetMatcher.BillMatchesText(b, t.Text))).ToList())
        {
            var retired = stale with { IsActive = false };
            await store.SaveBillAsync(retired, ct);
            known = known.Select(b => b.Id == stale.Id ? retired : b).ToList();
        }

        var bills = known.Where(b => b.IsActive).ToList();
        if (bills.Count == 0)
        {
            return new SheetRefreshResult(0, 0, added);
        }

        var result = SheetMatcher.Match(bills, existing, txns, months);
        if (result.Upserts.Count > 0 || result.Clears.Count > 0)
        {
            await store.ApplyPaymentsAsync(result.Upserts, result.Clears, ct);
        }

        return new SheetRefreshResult(result.Upserts.Count, result.Clears.Count, added);
    }

    private static SheetBillData RepeatingBill(Guid id, string section, int order, DetectedFlow f, string? service)
        => new(
            id, section, order, f.Name, null, false, true, false, false, false,
            f.Frequency == BillFrequency.TwiceMonthly ? f.DayOfMonth : null, f.Amount, f.Amount, service, null,
            RecurringDetector.MatchTextFor(f.Name), true,
            f.Frequency, f.IntervalWeeks,
            f.Frequency == BillFrequency.TwiceMonthly ? null : f.LastSeen,
            f.Frequency == BillFrequency.TwiceMonthly ? f.SecondDayOfMonth : null);

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
        var existing = (await store.ListBillsAsync(ct: ct)).Where(b => incoming.Contains(b.Section)).ToList();
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
