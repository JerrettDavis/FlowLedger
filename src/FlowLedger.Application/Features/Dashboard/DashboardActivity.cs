using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.Application.Features.Dashboard;

/// <summary>A transaction surfaced for attention, with human-readable reasons.</summary>
public sealed record FlaggedTransactionDto(TransactionDto Transaction, IReadOnlyList<string> Reasons);

/// <summary>At-a-glance activity feed across all accounts.</summary>
public sealed record DashboardActivityDto(
    IReadOnlyList<TransactionDto> RecentPosted,
    IReadOnlyList<TransactionDto> Pending,
    decimal PendingNetAmount,
    IReadOnlyList<TransactionDto> Large,
    decimal LargeThreshold,
    IReadOnlyList<FlaggedTransactionDto> Suspicious,
    decimal MoneyInLast30Days,
    decimal MoneyOutLast30Days,
    int TransfersExcluded = 0);

/// <summary>
/// Pure heuristics that turn raw transactions into dashboard insights. No I/O so it is trivially testable.
/// "Suspicious" means "worth a second look", not proven fraud.
/// </summary>
public static class DashboardActivityAnalyzer
{
    public const int WindowDays = 30;
    public const int HistoryDays = 90;
    private const decimal MinLargeThreshold = 500m;
    private const decimal NewMerchantThreshold = 250m;
    private const decimal OutlierMultiple = 3m;
    private const decimal OutlierMinExcess = 50m;
    private const int DuplicateWindowDays = 3;

    public static DashboardActivityDto Analyze(
        IReadOnlyList<TransactionDto> all, DateOnly today, int listSize = 8, IReadOnlySet<Guid>? transferIds = null)
    {
        // Planned rows are forecast placeholders, never real activity.
        var real = all.Where(t => t.Status != "Planned" && t.Status != "Skipped").ToList();
        var windowStart = today.AddDays(-WindowDays);

        bool IsPending(TransactionDto t) => t.Status == "Pending";
        DateOnly Date(TransactionDto t) => t.PostedDate ?? t.EffectiveDate;
        decimal Signed(TransactionDto t) => t.Direction == "Debit" ? -t.Amount : t.Amount;

        var pending = real.Where(IsPending).OrderByDescending(t => t.EffectiveDate).ToList();
        var recent = real.Where(t => !IsPending(t)).OrderByDescending(Date).ThenByDescending(t => t.CreatedAt)
            .Take(listSize).ToList();

        // Transfers between the user's own accounts are not income or spending; keep them in the feed but out of totals.
        var transfers = transferIds ?? new HashSet<Guid>();
        var counted = real.Where(t => !transfers.Contains(t.Id)).ToList();
        var allInWindow = real.Where(t => Date(t) >= windowStart && Date(t) <= today).ToList();
        var inWindow = allInWindow.Where(t => !transfers.Contains(t.Id)).ToList();
        var moneyIn = inWindow.Where(t => t.Direction == "Credit").Sum(t => t.Amount);
        var moneyOut = inWindow.Where(t => t.Direction == "Debit").Sum(t => t.Amount);

        var threshold = LargeThreshold(counted.Where(t => Date(t) >= today.AddDays(-HistoryDays)).Select(t => t.Amount).ToList());
        var large = inWindow.Where(t => t.Amount >= threshold)
            .OrderByDescending(t => t.Amount).Take(listSize).ToList();

        var suspicious = FindSuspicious(counted, inWindow, threshold, today, listSize);

        return new DashboardActivityDto(
            recent, pending.Take(listSize * 2).ToList(), pending.Sum(Signed),
            large, threshold, suspicious, moneyIn, moneyOut,
            allInWindow.Count(t => transfers.Contains(t.Id)) / 2);
    }

    /// <summary>95th percentile of recent amounts, floored so tiny datasets don't flag everything.</summary>
    public static decimal LargeThreshold(IReadOnlyList<decimal> amounts)
    {
        if (amounts.Count == 0)
        {
            return MinLargeThreshold;
        }

        var sorted = amounts.OrderBy(a => a).ToList();
        var idx = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
        return Math.Max(MinLargeThreshold, sorted[Math.Clamp(idx, 0, sorted.Count - 1)]);
    }

    private static string Key(TransactionDto t) =>
        (string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName).Trim().ToUpperInvariant();

    private static IReadOnlyList<FlaggedTransactionDto> FindSuspicious(
        List<TransactionDto> real, List<TransactionDto> candidates, decimal threshold, DateOnly today, int take)
    {
        var historyStart = today.AddDays(-HistoryDays);
        var history = real.Where(t => (t.PostedDate ?? t.EffectiveDate) >= historyStart).ToList();
        var flagged = new List<FlaggedTransactionDto>();

        foreach (var t in candidates.Where(c => c.Direction == "Debit"))
        {
            var reasons = new List<string>();
            var key = Key(t);
            var date = t.PostedDate ?? t.EffectiveDate;
            var others = history.Where(o => o.Id != t.Id).ToList();

            if (others.Any(o => o.AccountId == t.AccountId && o.Direction == t.Direction && o.Amount == t.Amount
                && Key(o) == key && Math.Abs(((o.PostedDate ?? o.EffectiveDate).DayNumber) - date.DayNumber) <= DuplicateWindowDays))
            {
                reasons.Add("Possible duplicate charge");
            }

            var sameMerchant = others.Where(o => o.Direction == "Debit" && Key(o) == key).Select(o => o.Amount).OrderBy(a => a).ToList();
            if (sameMerchant.Count >= 3)
            {
                var median = sameMerchant[sameMerchant.Count / 2];
                if (median > 0 && t.Amount > median * OutlierMultiple && t.Amount - median >= OutlierMinExcess)
                {
                    reasons.Add($"{t.Amount / median:0.#}× the usual amount for this merchant");
                }
            }
            else if (sameMerchant.Count == 0 && t.Amount >= Math.Max(NewMerchantThreshold, threshold / 2))
            {
                reasons.Add("Large charge from a merchant not seen before");
            }

            if (reasons.Count > 0)
            {
                flagged.Add(new FlaggedTransactionDto(t, reasons));
            }
        }

        return flagged.OrderByDescending(f => f.Reasons.Count).ThenByDescending(f => f.Transaction.Amount)
            .Take(take).ToList();
    }
}

/// <summary>Loads recent history and runs the analyzer.</summary>
public sealed class GetDashboardActivityHandler(ITransactionRepository repo, IAccountRepository accounts)
{
    public async Task<DashboardActivityDto> HandleAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-DashboardActivityAnalyzer.HistoryDays);
        var txs = await repo.ListAsync(null, from, today.AddDays(7), 0, 5000, ct);
        var dtos = txs.Select(TransactionMapper.ToDto).ToList();
        var types = (await accounts.ListAsync(ct)).ToDictionary(a => a.Id, a => a.AccountType.ToString());
        return DashboardActivityAnalyzer.Analyze(dtos, today, transferIds: TransferDetector.Detect(dtos, types));
    }
}
