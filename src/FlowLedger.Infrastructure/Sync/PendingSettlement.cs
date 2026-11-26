using FlowLedger.Domain.Aggregates;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Pure rules for matching a posted provider row to the pending authorization it replaces, and for retiring stale holds
/// (#104). Extracted from FinancialSyncService (#103) so the sync service keeps only orchestration.
/// </summary>
internal static class PendingSettlement
{
    /// <summary>Largest amount a posted total may exceed its authorization by (restaurant/ride-share tips).</summary>
    internal const decimal TipTolerance = 0.30m;

    /// <summary>Pending authorizations older than this that the provider never settled are retired (#104).</summary>
    internal const int StalePendingDays = 14;

    internal static Transaction? FindTipSettleCandidate(
        Dictionary<(TransactionDirection, decimal), List<Transaction>> lookup,
        TransactionDirection direction, decimal postedAmount, ProviderTransaction posted, HashSet<string> stillPending)
    {
        var found = lookup
            .Where(kv => kv.Key.Item1 == direction && kv.Key.Item2 < postedAmount && postedAmount <= kv.Key.Item2 * (1m + TipTolerance))
            .SelectMany(kv => kv.Value)
            .Where(t => t.Status == TransactionStatus.Pending
                && (t.Fingerprint is null || !stillPending.Contains(t.Fingerprint.Value))
                && t.Amount.Currency == posted.Amount.Currency
                && t.EffectiveDate <= posted.PostedDate
                && t.EffectiveDate >= posted.PostedDate.AddDays(-7)
                && SharesMerchantToken(t.Description, posted.RawDescription))
            .Take(2)
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }

    private static bool SharesMerchantToken(string a, string b)
    {
        static HashSet<string> Tokens(string s) => s
            .Split([' ', '*', '#', '-', '/', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !w.All(char.IsDigit))
            .Select(w => w.ToUpperInvariant())
            .ToHashSet();
        return Tokens(a ?? string.Empty).Overlaps(Tokens(b ?? string.Empty));
    }

    internal static void RemoveSettleCandidate(Dictionary<(TransactionDirection, decimal), List<Transaction>> lookup, Transaction t)
    {
        foreach (var list in lookup.Values)
        {
            list.Remove(t);
        }
    }

    internal static void AddSettleCandidate(Dictionary<(TransactionDirection, decimal), List<Transaction>> lookup, Transaction t)
    {
        var key = (t.Direction, t.Amount.Amount);
        if (!lookup.TryGetValue(key, out var list))
        {
            lookup[key] = list = [];
        }

        list.Add(t);
    }
}
