using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Transactions;

namespace FlowLedger.Application.Features.Investments;

/// <summary>Net cash moved into (+) or out of (-) an investment account on a date, as seen by that account.</summary>
public sealed record CashFlowPoint(DateOnly Date, decimal Amount);

public sealed record PeriodPerformance(string Period, int Days, decimal StartBalance, decimal NetContributions, decimal Gain, double? GainPercent);

public sealed record AccountPerformance(Guid AccountId, string Name, decimal Balance, IReadOnlyList<PeriodPerformance> Periods);

/// <summary>
/// Investment growth separated from deposits. Gain = end balance - start balance - net contributions, so moving money in
/// is not mistaken for performance. Pure (no I/O): balances and contributions are passed in.
/// </summary>
public static class InvestmentPerformanceCalculator
{
    public static readonly IReadOnlyList<(string Label, int Days)> Periods = [("30 days", 30), ("90 days", 90), ("1 year", 365)];

    /// <summary>Gain over one user-chosen window [<paramref name="from"/>, <paramref name="to"/>]; the end balance is the latest snapshot on/before <paramref name="to"/>.</summary>
    public static IReadOnlyList<PeriodPerformance> ComputeRange(
        IReadOnlyList<BalancePoint> history, IReadOnlyList<CashFlowPoint> contributions, decimal currentBalance,
        DateOnly from, DateOnly to, DateOnly today)
    {
        var ordered = history.OrderBy(h => h.Date).ToList();
        if (ordered.Count == 0 || to <= from)
        {
            return [];
        }

        var end = to >= today ? currentBalance : (ordered.LastOrDefault(h => h.Date <= to)?.Balance ?? currentBalance);
        var anchor = ordered.LastOrDefault(h => h.Date <= from) ?? ordered[0];
        if (anchor.Date >= to)
        {
            return [];
        }

        var net = contributions.Where(c => c.Date > anchor.Date && c.Date <= to).Sum(c => c.Amount);
        var gain = end - anchor.Balance - net;
        double? pct = anchor.Balance > 0 ? (double)(gain / anchor.Balance * 100m) : null;
        return [new PeriodPerformance("Selected range", to.DayNumber - anchor.Date.DayNumber, anchor.Balance, net, gain, pct)];
    }

    public static IReadOnlyList<PeriodPerformance> Compute(
        IReadOnlyList<BalancePoint> history, IReadOnlyList<CashFlowPoint> contributions, decimal currentBalance, DateOnly today)
    {
        var ordered = history.OrderBy(h => h.Date).ToList();
        var result = new List<PeriodPerformance>();
        if (ordered.Count == 0)
        {
            return result;
        }

        foreach (var (label, days) in Periods)
        {
            var start = today.AddDays(-days);
            // Latest snapshot on/before the period start; if history is shorter than the period, use the earliest one.
            var anchor = ordered.LastOrDefault(h => h.Date <= start) ?? ordered[0];
            if (anchor.Date >= today)
            {
                continue;
            }

            var net = contributions.Where(c => c.Date > anchor.Date && c.Date <= today).Sum(c => c.Amount);
            var gain = currentBalance - anchor.Balance - net;
            double? pct = anchor.Balance > 0 ? (double)(gain / anchor.Balance * 100m) : null;
            result.Add(new PeriodPerformance(label, (today.DayNumber - anchor.Date.DayNumber), anchor.Balance, net, gain, pct));
        }

        return result;
    }
}

/// <summary>Builds per-account investment performance from balance snapshots and transfers into/out of each account.</summary>
public sealed class InvestmentPerformanceService(
    IAccountRepository accounts,
    ITransactionRepository transactions,
    IBalanceHistoryReader history)
{
    private const int Max = TransactionQuota.ProcessingCap;

    public async Task<IReadOnlyList<AccountPerformance>> GetAsync(DateOnly today, CancellationToken ct = default, DateOnly? from = null, DateOnly? to = null)
    {
        var investment = (await accounts.ListAsync(ct)).Where(a => a.IsActive && a.AccountType.ToString() == "Investment").ToList();
        if (investment.Count == 0)
        {
            return [];
        }

        var allAccounts = await accounts.ListAsync(ct);
        var accountTypes = allAccounts.ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var dtos = (await transactions.ListAllAsync(Max, ct))
            .Where(t => FlowLedger.Domain.Aggregates.TransactionStatusExtensions.IsReal(t.Status))
            .Select(TransactionMapper.ToDto).ToList();
        var transferIds = TransferDetector.Detect(dtos, accountTypes);
        var points = await history.DailyAccountBalancesAsync(ct);

        var list = new List<AccountPerformance>();
        foreach (var a in investment)
        {
            var flows = dtos.Where(t => t.AccountId == a.Id && transferIds.Contains(t.Id))
                .Select(t => new CashFlowPoint(t.PostedDate ?? t.EffectiveDate, t.Direction == "Credit" ? t.Amount : -t.Amount))
                .ToList();
            var accountPoints = points.Where(p => p.AccountId == a.Id).ToList();
            var periods = from is { } f
                ? InvestmentPerformanceCalculator.ComputeRange(accountPoints, flows, a.CurrentBalance.Amount, f, to ?? today, today)
                : InvestmentPerformanceCalculator.Compute(accountPoints, flows, a.CurrentBalance.Amount, today);
            list.Add(new AccountPerformance(a.Id, a.Name, a.CurrentBalance.Amount, periods));
        }

        return list;
    }
}
