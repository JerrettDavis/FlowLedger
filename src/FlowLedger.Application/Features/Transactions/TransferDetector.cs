namespace FlowLedger.Application.Features.Transactions;

/// <summary>
/// Pure transfer detection. A transfer is money moving between two of the user's OWN accounts (checking to savings,
/// a card or loan payment). Both legs show up as separate transactions, so counting them as income/spending double-counts.
/// Detection is read-time only: nothing is written or merged, so a wrong guess can never lose data.
///
/// Two legs match when they are on different accounts, have the same amount, fall within <see cref="WindowDays"/> days of
/// each other, and either (a) have opposite directions, or (b) are both debits where one side is a liability account
/// (providers differ on how they sign a payment received by a loan/card) and the descriptions agree.
/// Matching is one-to-one, nearest date first.
/// </summary>
public static class TransferDetector
{
    public const int WindowDays = 3;

    private static readonly HashSet<string> LiabilityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreditCard", "Loan", "Mortgage", "ManualLiability",
    };

    public static HashSet<Guid> Detect(
        IReadOnlyList<TransactionDto> transactions,
        IReadOnlyDictionary<Guid, string> accountTypeById)
    {
        var result = new HashSet<Guid>();
        var real = transactions
            .Where(t => t.Status is not ("Planned" or "Skipped"))
            .OrderBy(t => t.PostedDate ?? t.EffectiveDate)
            .ToList();

        foreach (var group in real.GroupBy(t => t.Amount))
        {
            var legs = group.ToList();
            if (legs.Count < 2)
            {
                continue;
            }

            var used = new HashSet<Guid>();
            foreach (var a in legs)
            {
                if (used.Contains(a.Id))
                {
                    continue;
                }

                var best = legs
                    .Where(b => b.Id != a.Id && !used.Contains(b.Id) && IsMatch(a, b, accountTypeById))
                    .OrderBy(b => Math.Abs(Day(b) - Day(a)))
                    .FirstOrDefault();
                if (best is null)
                {
                    continue;
                }

                used.Add(a.Id);
                used.Add(best.Id);
                result.Add(a.Id);
                result.Add(best.Id);
            }
        }

        return result;
    }

    private static int Day(TransactionDto t) => (t.PostedDate ?? t.EffectiveDate).DayNumber;

    private static bool IsMatch(TransactionDto a, TransactionDto b, IReadOnlyDictionary<Guid, string> types)
    {
        if (a.AccountId == b.AccountId || Math.Abs(Day(a) - Day(b)) > WindowDays || a.Currency != b.Currency)
        {
            return false;
        }

        if (a.Direction != b.Direction)
        {
            return true;
        }

        // Same direction: only a payment hitting a liability account, with agreeing descriptions.
        var aLiab = types.TryGetValue(a.AccountId, out var ta) && LiabilityTypes.Contains(ta);
        var bLiab = types.TryGetValue(b.AccountId, out var tb) && LiabilityTypes.Contains(tb);
        return aLiab != bLiab
            && a.Direction == "Debit"
            && string.Equals(Name(a), Name(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Name(TransactionDto t) =>
        (string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName).Trim();
}
