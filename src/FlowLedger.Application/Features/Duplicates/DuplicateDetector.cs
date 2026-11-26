using FlowLedger.Application.Features.LocalAi;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Duplicates;

/// <summary>How sure the detector is that two rows are one real-world transaction.</summary>
public enum DuplicateConfidence
{
    /// <summary>Plausible but could be two genuine purchases; only ever suggested, never merged without a click.</summary>
    Possible,

    /// <summary>Unambiguous: safe for the automatic merge.</summary>
    High,
}

/// <summary>One suspected duplicate pair. <see cref="Survivor"/> is the row that is kept; <see cref="Duplicate"/> is folded into it.</summary>
public sealed record DuplicatePair(
    Transaction Survivor, Transaction Duplicate, DuplicateConfidence Confidence, double Score, int DaysApart, string Reason)
{
    /// <summary>Stable across scans (order-independent), so a dismissal keeps hiding the same pair.</summary>
    public string Key => MakeKey(Survivor.Id, Duplicate.Id);

    public static string MakeKey(Guid a, Guid b) => a.CompareTo(b) < 0 ? $"{a:N}:{b:N}" : $"{b:N}:{a:N}";
}

/// <summary>
/// Pure duplicate finder. Two rows are candidates only when they are on the SAME account with the same direction and the
/// same amount, a few days apart. Rows that the bank itself reports with two different ids are never auto-merged (the bank
/// says they are distinct; a $2.60 vending machine used twice is common), and a row that has been matched to a plan or
/// reconciled is never the one that is deleted. A duplicate is a recoverable annoyance; a wrong merge loses data, so
/// every ambiguity falls back to "suggest".
/// </summary>
public static class DuplicateDetector
{
    public const int MaxDaysApart = 3;
    public const int MaxPendingToPostedDays = 7;

    public static IReadOnlyList<DuplicatePair> Find(IEnumerable<Transaction> transactions)
    {
        var rows = transactions
            .Where(t => t.Status is TransactionStatus.Pending or TransactionStatus.Posted
                or TransactionStatus.Matched or TransactionStatus.Reconciled)
            .ToList();

        var candidates = new List<Candidate>();
        foreach (var g in rows.GroupBy(t => (t.AccountId.Value, t.Direction, t.Amount.Amount, t.Amount.Currency.Code)))
        {
            var list = g.OrderBy(t => t.EffectiveDate).ThenBy(t => t.Id).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var days = list[j].EffectiveDate.DayNumber - list[i].EffectiveDate.DayNumber;
                    if (days > MaxPendingToPostedDays)
                    {
                        break;
                    }

                    var c = Evaluate(list[i], list[j], days);
                    if (c is not null)
                    {
                        candidates.Add(c);
                    }
                }
            }
        }

        // A row that could pair with several others is ambiguous: its pairs stay suggestions.
        var degree = new Dictionary<Guid, int>();
        foreach (var c in candidates)
        {
            degree[c.A.Id] = degree.GetValueOrDefault(c.A.Id) + 1;
            degree[c.B.Id] = degree.GetValueOrDefault(c.B.Id) + 1;
        }

        // Each row joins at most one pair, best score first.
        var used = new HashSet<Guid>();
        var result = new List<DuplicatePair>();
        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.A.Id).ThenBy(c => c.B.Id))
        {
            if (used.Contains(c.A.Id) || used.Contains(c.B.Id))
            {
                continue;
            }

            var ambiguous = degree[c.A.Id] > 1 || degree[c.B.Id] > 1;
            var (survivor, duplicate) = ChooseSurvivor(c.A, c.B);
            var confidence = c.High && !ambiguous && CanAutoRemove(duplicate) ? DuplicateConfidence.High : DuplicateConfidence.Possible;
            var reason = c.Reason + (ambiguous ? " (several similar rows; review)" : string.Empty);
            result.Add(new DuplicatePair(survivor, duplicate, confidence, Math.Round(c.Score, 3), c.Days, reason));
            used.Add(c.A.Id);
            used.Add(c.B.Id);
        }

        return result.OrderByDescending(p => p.Confidence).ThenByDescending(p => p.Score)
            .ThenByDescending(p => p.Survivor.EffectiveDate).ToList();
    }

    private sealed record Candidate(Transaction A, Transaction B, double Score, bool High, int Days, string Reason);

    private static bool HasProviderId(Transaction t) => t.Fingerprint?.Value.StartsWith("pid:", StringComparison.Ordinal) == true;

    private static bool Protected(Transaction t) => t.Status is TransactionStatus.Matched or TransactionStatus.Reconciled || t.MatchedOccurrenceId is not null;

    private static bool CanAutoRemove(Transaction t) => !Protected(t) && t.Splits.Count == 0;

    private static Candidate? Evaluate(Transaction a, Transaction b, int days)
    {
        if (Protected(a) && Protected(b))
        {
            return null; // both carry user/plan state; a person must decide
        }

        var sim = Similarity(a, b);
        var pidA = HasProviderId(a);
        var pidB = HasProviderId(b);
        var pending = a.Status == TransactionStatus.Pending ^ b.Status == TransactionStatus.Pending;
        var bothPending = a.Status == TransactionStatus.Pending && b.Status == TransactionStatus.Pending;
        var bothManual = a.CategorySource == CategorySource.Manual && b.CategorySource == CategorySource.Manual
            && a.CategoryId is not null && b.CategoryId is not null && a.CategoryId != b.CategoryId;

        if (pidA && pidB)
        {
            if (pending)
            {
                // The bank re-issued a posted row under a new id while the pending one lingers. The sync absorbs the
                // unambiguous case; whatever is left here is a pending ghost of a posted row.
                var pend = a.Status == TransactionStatus.Pending ? a : b;
                var post = ReferenceEquals(pend, a) ? b : a;
                if (post.EffectiveDate >= pend.EffectiveDate && sim >= 0.5 && days <= MaxPendingToPostedDays)
                {
                    return new Candidate(a, b, 0.7 + (0.3 * sim), !bothManual, days, "A pending row and a posted row with the same amount and similar description (the pending one never settled)");
                }

                return null;
            }

            // Two ids the bank itself issued: only worth a look when everything else is identical.
            if (days == 0 && !bothPending && sim >= 0.95)
            {
                return new Candidate(a, b, 0.45, false, days, "Same account, day, amount and description, but the bank gave them different ids (may be two real purchases)");
            }

            return null;
        }

        if (days > MaxDaysApart)
        {
            return null;
        }

        if (pidA != pidB)
        {
            // One from the bank, one typed in or imported from a file: the classic overlap.
            if (sim >= 0.5 && days <= 2)
            {
                return new Candidate(a, b, 0.8 + (0.2 * sim) - (0.03 * days), !bothManual, days, "An imported/manual row matches a bank-synced row (same account, amount, date and similar description)");
            }

            return sim >= 0.34
                ? new Candidate(a, b, 0.4 + (0.3 * sim), false, days, "An imported/manual row may match a bank-synced row (weaker description match)")
                : null;
        }

        // Neither has a bank id (two files with different formatting, or manual duplicates).
        if (days == 0 && sim >= 0.6)
        {
            return new Candidate(a, b, 0.75 + (0.25 * sim), !bothManual, days, "Same account, day and amount with a near-identical description");
        }

        return sim >= 0.5 ? new Candidate(a, b, 0.4 + (0.3 * sim) - (0.02 * days), false, days, "Same account and amount within a few days with a similar description") : null;
    }

    /// <summary>Picks the row to keep: plan-linked/reconciled, then user-edited, then posted, then bank-issued, then oldest.</summary>
    internal static (Transaction Survivor, Transaction Duplicate) ChooseSurvivor(Transaction a, Transaction b)
    {
        int Rank(Transaction t) =>
            (Protected(t) ? 1000 : 0)
            + (t.Splits.Count > 0 ? 400 : 0)
            + (t.CategorySource == CategorySource.Manual && t.CategoryId is not null ? 200 : 0)
            + (!string.IsNullOrEmpty(t.Notes) ? 100 : 0)
            + (t.Status != TransactionStatus.Pending ? 50 : 0)
            + (HasProviderId(t) ? 25 : 0);

        var ra = Rank(a);
        var rb = Rank(b);
        if (ra != rb)
        {
            return ra > rb ? (a, b) : (b, a);
        }

        return a.CreatedAt <= b.CreatedAt ? (a, b) : (b, a);
    }

    /// <summary>0..1 token overlap of the normalised descriptors (max of Jaccard and 0.9 x containment).</summary>
    internal static double Similarity(Transaction a, Transaction b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return string.Equals(a.Description.Trim(), b.Description.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        var inter = ta.Intersect(tb).Count();
        var jaccard = inter / (double)ta.Union(tb).Count();
        var containment = inter / (double)Math.Min(ta.Count, tb.Count);
        return Math.Max(jaccard, 0.9 * containment);
    }

    // Both the raw descriptor and any merchant alias count, so a renamed row still matches its un-renamed twin.
    private static HashSet<string> Tokens(Transaction t) =>
        $"{DescriptorNormalizer.Normalize(null, t.Description)} {DescriptorNormalizer.Normalize(t.MerchantName, null)}"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
}
