using System.Globalization;
using System.Text.RegularExpressions;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.Application.Features.Insights;

/// <summary>
/// Pure statistical anomaly rules over a household's own history (robust median/MAD, no training step, no I/O).
/// "Anomaly" means worth a second look, not proven fraud. Returns human-readable reasons per transaction id.
/// </summary>
public static class AnomalyDetector
{
    public const string PriceIncreasePrefix = "Price increase";
    public const string MerchantOutlierPrefix = "Unusual amount for this merchant";
    public const string CategoryOutlierPrefix = "Unusually large for";

    private const int MinMerchantSamples = 5;
    private const int MinCategorySamples = 10;
    private const double ZThreshold = 5.0;
    private const double MadToSigma = 1.4826;

    private static readonly Regex NonLetters = new(@"[^A-Z&\s]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Merchant key with store numbers/ids stripped so "SHELL #123" and "SHELL #456" group together.</summary>
    public static string MerchantKey(TransactionDto t)
    {
        var raw = string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName;
        return Spaces.Replace(NonLetters.Replace(raw.ToUpperInvariant(), " "), " ").Trim();
    }

    /// <param name="history">Real (non-planned, non-transfer) transactions, any direction; debits are analysed.</param>
    /// <param name="candidates">The transactions to judge (a subset of history, typically the last 30 days).</param>
    public static IReadOnlyDictionary<Guid, List<string>> Detect(
        IReadOnlyList<TransactionDto> history,
        IReadOnlyList<TransactionDto> candidates,
        IReadOnlyDictionary<Guid, string> categoryPathById)
    {
        var debits = history.Where(t => t.Direction == "Debit" && t.Amount > 0).ToList();
        var byMerchant = debits.Where(t => MerchantKey(t).Length > 0).ToLookup(MerchantKey);
        var byCategory = debits.Where(t => t.CategoryId is not null).ToLookup(t => t.CategoryId!.Value);
        var result = new Dictionary<Guid, List<string>>();

        void Add(Guid id, string reason)
        {
            if (!result.TryGetValue(id, out var l))
            {
                result[id] = l = [];
            }

            l.Add(reason);
        }

        foreach (var t in candidates.Where(c => c.Direction == "Debit" && c.Amount > 0))
        {
            // Money moving between accounts / into brokerage / paying a card is not spending; never anomalous.
            if (t.CategoryId is { } own && categoryPathById.TryGetValue(own, out var ownPath) && SpendingInsightsAnalyzer.IsNonSpending(ownPath))
            {
                continue;
            }

            var date = Date(t);
            var key = MerchantKey(t);
            var prior = key.Length == 0
                ? []
                : byMerchant[key].Where(o => o.Id != t.Id && Date(o) < date).OrderBy(Date).ToList();

            // 1. Subscription price increase: a steady monthly/weekly charge that just went up.
            if (prior.Count >= 3 && IsRegular(prior.TakeLast(6).ToList(), date, out var typical)
                && t.Amount >= typical * 1.05m && t.Amount - typical >= 1m)
            {
                Add(t.Id, $"{PriceIncreasePrefix}: {Money(t.Amount)} vs {Money(typical)} for the last {Math.Min(prior.Count, 6)} charges");
                continue;
            }

            // 2. Merchant outlier, scaled to this merchant's own variability.
            if (prior.Count >= MinMerchantSamples && IsOutlier(prior.Select(o => o.Amount).ToList(), t.Amount, 3m, 40m, out var med))
            {
                Add(t.Id, $"{MerchantOutlierPrefix}: {Money(t.Amount)} vs typical {Money(med)}");
                continue;
            }

            // 3. Category outlier: catches a big one-off from a new merchant in a normally small category.
            if (t.CategoryId is { } cid && categoryPathById.TryGetValue(cid, out var path))
            {
                var peers = byCategory[cid].Where(o => o.Id != t.Id && Date(o) < date).Select(o => o.Amount).ToList();
                if (peers.Count >= MinCategorySamples && IsOutlier(peers, t.Amount, 3m, 100m, out var catMed))
                {
                    Add(t.Id, $"{CategoryOutlierPrefix} {path}: {Money(t.Amount)} vs typical {Money(catMed)}");
                }
            }
        }

        return result;
    }

    private static DateOnly Date(TransactionDto t) => t.PostedDate ?? t.EffectiveDate;

    private static string Money(decimal v) => v.ToString("C2", CultureInfo.GetCultureInfo("en-US"));

    private static bool IsOutlier(IReadOnlyList<decimal> sample, decimal value, decimal minRatio, decimal minExcess, out decimal median)
    {
        var m = Median(sample);
        median = m;
        if (m <= 0)
        {
            return false;
        }

        var mad = Median(sample.Select(s => Math.Abs(s - m)).ToList());
        var sigma = Math.Max((double)mad * MadToSigma, (double)m * 0.05); // floor avoids divide-by-~0 on constant amounts
        var z = (double)(value - m) / sigma;
        return z >= ZThreshold && value >= m * minRatio && value - m >= minExcess;
    }

    /// <summary>True when the last charges recur on a weekly or monthly rhythm at a near-constant amount ending near <paramref name="next"/>.</summary>
    private static bool IsRegular(IReadOnlyList<TransactionDto> last, DateOnly next, out decimal typical)
    {
        var t = Median(last.Select(o => o.Amount).ToList());
        typical = t;
        if (t <= 0 || last.Any(o => Math.Abs(o.Amount - t) > t * 0.03m))
        {
            return false;
        }

        var gaps = new List<int>();
        for (var i = 1; i < last.Count; i++)
        {
            gaps.Add(Date(last[i]).DayNumber - Date(last[i - 1]).DayNumber);
        }

        gaps.Add(next.DayNumber - Date(last[^1]).DayNumber);
        var gap = gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
        var monthly = gap is >= 26 and <= 35;
        var weekly = gap is >= 6 and <= 8;
        return (monthly || weekly) && gaps.All(g => Math.Abs(g - gap) <= (monthly ? 5 : 2));
    }

    private static decimal Median(IReadOnlyList<decimal> v)
    {
        if (v.Count == 0)
        {
            return 0;
        }

        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
