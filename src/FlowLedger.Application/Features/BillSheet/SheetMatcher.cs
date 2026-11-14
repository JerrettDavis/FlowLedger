using System.Text;

namespace FlowLedger.Application.Features.BillSheet;

/// <summary>
/// Pure, deterministic matcher that decides which transactions pay which (bill, month) cells.
/// See docs/superpowers/specs/2026-11-13-spreadsheet-mode-design.md.
/// </summary>
public static class SheetMatcher
{
    /// <summary>Largest gap (days) between a transaction and a bill's expected date in a month.</summary>
    public const int MaxDueDayDistance = 7;

    public static SheetMatchResult Match(
        IReadOnlyList<SheetBillData> bills,
        IReadOnlyList<SheetPaymentData> existing,
        IReadOnlyList<SheetTxn> transactions,
        IReadOnlyList<DateOnly> months)
    {
        var monthSet = months.Select(FirstOfMonth).ToHashSet();
        var activeBills = bills.Where(b => b.IsActive).OrderBy(b => b.Section, StringComparer.Ordinal)
            .ThenBy(b => b.SortOrder).ThenBy(b => b.Id).ToList();
        var billById = activeBills.ToDictionary(b => b.Id);
        var txnById = transactions.ToDictionary(t => t.Id);
        var terms = activeBills.ToDictionary(b => b.Id, Terms);

        var clears = new List<(Guid, DateOnly)>();
        var occupied = new HashSet<(Guid, DateOnly)>();
        var claimed = new HashSet<Guid>();

        // Manual marks win: claim their transactions first so an auto mark can never steal one.
        foreach (var p in existing.Where(p => p.Source == SheetPaymentSource.Manual))
        {
            occupied.Add((p.BillId, FirstOfMonth(p.Month)));
            if (p.TransactionId is { } manualTid)
            {
                claimed.Add(manualTid);
            }
        }

        foreach (var p in existing.Where(p => p.Source == SheetPaymentSource.Auto)
            .OrderBy(p => p.BillId).ThenBy(p => p.Month))
        {
            var month = FirstOfMonth(p.Month);
            if (!monthSet.Contains(month))
            {
                occupied.Add((p.BillId, month));
                if (p.TransactionId is { } outside)
                {
                    claimed.Add(outside);
                }

                continue;
            }

            // Re-validate automatic marks: the transaction must still exist and still match.
            var valid = p.TransactionId is { } tid
                && txnById.TryGetValue(tid, out var t)
                && billById.ContainsKey(p.BillId)
                && Matches(terms[p.BillId], t.Text)
                && !claimed.Contains(tid);
            if (!valid)
            {
                clears.Add((p.BillId, month));
                continue;
            }

            claimed.Add(p.TransactionId!.Value);
            occupied.Add((p.BillId, month));
        }

        var edges = new List<Edge>();
        foreach (var bill in activeBills)
        {
            var billTerms = terms[bill.Id];
            if (billTerms.Count == 0)
            {
                continue;
            }

            foreach (var txn in transactions)
            {
                if (claimed.Contains(txn.Id) || !Matches(billTerms, txn.Text))
                {
                    continue;
                }

                foreach (var month in monthSet)
                {
                    if (occupied.Contains((bill.Id, month)))
                    {
                        continue;
                    }

                    var distance = Distance(bill.DueDay, month, txn.Date);
                    if (distance is null)
                    {
                        continue;
                    }

                    edges.Add(new Edge(bill, month, txn, distance.Value, Math.Abs(txn.Amount - bill.Estimated)));
                }
            }
        }

        var upserts = new List<SheetPaymentData>();
        foreach (var e in edges
            .OrderBy(e => e.Distance).ThenBy(e => e.AmountGap).ThenBy(e => e.Txn.Date)
            .ThenBy(e => e.Txn.Id).ThenBy(e => e.Bill.Section, StringComparer.Ordinal)
            .ThenBy(e => e.Bill.SortOrder).ThenBy(e => e.Bill.Id).ThenBy(e => e.Month))
        {
            if (claimed.Contains(e.Txn.Id) || occupied.Contains((e.Bill.Id, e.Month)))
            {
                continue;
            }

            claimed.Add(e.Txn.Id);
            occupied.Add((e.Bill.Id, e.Month));
            upserts.Add(new SheetPaymentData(
                e.Bill.Id, e.Month, true, e.Txn.Amount, e.Txn.Date, e.Txn.Id, SheetPaymentSource.Auto));
        }

        return new SheetMatchResult(upserts, clears);
    }

    public static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>Expected payment date of a bill in a month, with the day clamped to the month's length.</summary>
    public static DateOnly? ExpectedDate(int? dueDay, DateOnly month)
        => dueDay is { } d and >= 1 and <= 31
            ? new DateOnly(month.Year, month.Month, Math.Min(d, DateTime.DaysInMonth(month.Year, month.Month)))
            : null;

    private static int? Distance(int? dueDay, DateOnly month, DateOnly txnDate)
    {
        if (ExpectedDate(dueDay, month) is { } expected)
        {
            var days = Math.Abs(txnDate.DayNumber - expected.DayNumber);
            return days <= MaxDueDayDistance ? days : null;
        }

        // Flux bills match only inside the month.
        return FirstOfMonth(txnDate) == month ? 0 : null;
    }

    internal static IReadOnlyList<string> Terms(SheetBillData bill)
    {
        string raw;
        if (!string.IsNullOrWhiteSpace(bill.MatchText))
        {
            raw = bill.MatchText;
        }
        else if (IsCardSection(bill.Section))
        {
            // Store names on card rows (Amazon, Best Buy...) match purchases, not payments: require explicit text.
            return [];
        }
        else
        {
            raw = DefaultAlternatives(bill.Name);
        }

        return raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).Where(s => s.Length >= 2).Distinct().ToList();
    }

    public static bool IsCardSection(string section) => section.Contains("credit card", StringComparison.OrdinalIgnoreCase);

    /// <summary>"AEP (AMER ELECT PWR)" gives the whole name, the part before the parentheses and the part inside them.</summary>
    private static string DefaultAlternatives(string name)
    {
        var alts = new List<string> { name };
        var open = name.IndexOf('(', StringComparison.Ordinal);
        var close = open >= 0 ? name.IndexOf(')', open + 1) : -1;
        if (open > 0 && close > open)
        {
            alts.Add(name[..open]);
            alts.Add(name[(open + 1)..close]);
        }

        return string.Join('|', alts);
    }

    private static bool Matches(IReadOnlyList<string> terms, string text)
    {
        if (terms.Count == 0)
        {
            return false;
        }

        var haystack = Normalize(text);
        return terms.Any(t => haystack.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>Lower-case letters and digits only, so "AT&amp;T  Wireless" matches "att wireless".</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private sealed record Edge(SheetBillData Bill, DateOnly Month, SheetTxn Txn, int Distance, decimal AmountGap);
}
