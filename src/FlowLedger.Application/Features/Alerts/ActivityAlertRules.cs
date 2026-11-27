using FlowLedger.Application.Features.Dashboard;
using FlowLedger.Application.Features.Insights;
using FlowLedger.Application.Features.Shared;

namespace FlowLedger.Application.Features.Alerts;

/// <summary>Kinds of transaction-driven alerts (#70). The wire value is the snake_case string used for Home Assistant events.</summary>
public static class ActivityAlertKinds
{
    public const string LargeCharge = "large_charge";
    public const string PriceIncrease = "price_increase";
    public const string NewSubscription = "new_subscription";
    public const string LowBalance = "low_balance";
}

/// <summary>An alert derived from recent activity. <see cref="DedupeKey"/> is stable, so a condition fires once, not on every run.</summary>
public sealed record ActivityAlert(string Kind, string Severity, string Title, string Detail, string DedupeKey, string? Subject, DateOnly Date, decimal? Amount);

/// <summary>An account balance as the low-balance rule sees it.</summary>
internal sealed record BalanceInput(Guid Id, string Name, decimal Balance, bool IsLiquid);

/// <summary>
/// Pure rules that turn recent transactions and balances into large-charge, price-increase, new-subscription and low-balance alerts.
/// Bill-due, over-budget and overdraft alerts already exist (see <see cref="AlertBuilder"/> and the Home Assistant detector).
/// </summary>
internal static class ActivityAlertRules
{
    /// <summary>How far back a charge still counts as news.</summary>
    public const int RecentDays = 3;

    /// <summary>Default balance (in the account's currency) below which a liquid account is "low".</summary>
    public const decimal DefaultLowBalance = 100m;

    private const int HistoryDays = 90;
    private const int SubscriptionLookbackDays = 75;

    public static IReadOnlyList<ActivityAlert> Build(
        DateOnly today,
        IReadOnlyList<TransactionDto> history,
        IReadOnlySet<Guid> transferIds,
        IReadOnlyList<BalanceInput> balances,
        IReadOnlyDictionary<Guid, string> categoryPathById,
        decimal lowBalanceThreshold = DefaultLowBalance)
    {
        var alerts = new List<ActivityAlert>();
        var real = history
            .Where(t => FlowLedger.Domain.Aggregates.TransactionStatusExtensions.IsRealStatus(t.Status) && !transferIds.Contains(t.Id))
            .ToList();
        static DateOnly Day(TransactionDto t) => t.PostedDate ?? t.EffectiveDate;
        var recent = real.Where(t => Day(t) >= today.AddDays(-RecentDays) && Day(t) <= today).ToList();
        var debits = nameof(FlowLedger.Domain.Aggregates.TransactionDirection.Debit);

        // Large charge: at or above what is unusual for this household (the dashboard's own threshold), among the last few days.
        var threshold = DashboardActivityAnalyzer.LargeThreshold(
            real.Where(t => Day(t) >= today.AddDays(-HistoryDays)).Select(t => t.Amount).ToList());
        foreach (var t in recent.Where(t => t.Direction == debits && t.Amount >= threshold))
        {
            var name = NameOf(t);
            alerts.Add(new ActivityAlert(ActivityAlertKinds.LargeCharge, "warning", "Large charge",
                $"{name} charged {t.Amount:C0} on {Day(t):MMM d}.", $"{ActivityAlertKinds.LargeCharge}:{t.Id}", name, Day(t), t.Amount));
        }

        // Price increase: the anomaly detector's steady-charge rule, on recent charges only.
        var reasons = AnomalyDetector.Detect(real, recent, categoryPathById);
        foreach (var t in recent.Where(t => reasons.TryGetValue(t.Id, out var r) && r.Any(x => x.StartsWith(AnomalyDetector.PriceIncreasePrefix, StringComparison.Ordinal))))
        {
            var name = NameOf(t);
            var detail = reasons[t.Id].First(x => x.StartsWith(AnomalyDetector.PriceIncreasePrefix, StringComparison.Ordinal));
            alerts.Add(new ActivityAlert(ActivityAlertKinds.PriceIncrease, "warning", "Price increase", $"{name}: {detail}",
                $"{ActivityAlertKinds.PriceIncrease}:{t.Id}", name, Day(t), t.Amount));
        }

        // New subscription: a merchant first charged within the last ~2.5 months that has now repeated at a monthly rhythm for a
        // similar amount, with the latest charge just landing.
        foreach (var group in real.Where(t => t.Direction == debits).GroupBy(t => MerchantKey.From(NameOf(t))).Where(g => g.Key.Length >= 4))
        {
            var charges = group.OrderBy(Day).ToList();
            var first = charges[0];
            var latest = charges[^1];
            if (charges.Count < 2 || Day(first) < today.AddDays(-SubscriptionLookbackDays) || Day(latest) < today.AddDays(-RecentDays) || Day(latest) > today)
            {
                continue;
            }

            var gap = Day(latest).DayNumber - Day(charges[^2]).DayNumber;
            var similar = charges[^2].Amount > 0 && Math.Abs(latest.Amount - charges[^2].Amount) <= charges[^2].Amount * 0.1m;
            if (gap is >= 25 and <= 35 && similar && charges.Count <= 4)
            {
                var name = NameOf(latest);
                alerts.Add(new ActivityAlert(ActivityAlertKinds.NewSubscription, "info", "New subscription",
                    $"{name} now charges about {latest.Amount:C0} every month.", $"{ActivityAlertKinds.NewSubscription}:{group.Key}", name, Day(latest), latest.Amount));
            }
        }

        // Low balance: a liquid account that is still positive but under the threshold (overdrawn accounts already raise an overdraft alert).
        foreach (var a in balances.Where(a => a.IsLiquid && a.Balance >= 0 && a.Balance < lowBalanceThreshold))
        {
            alerts.Add(new ActivityAlert(ActivityAlertKinds.LowBalance, "warning", "Low balance",
                $"{a.Name} is down to {a.Balance:C0}.", $"{ActivityAlertKinds.LowBalance}:{a.Id}:{today:yyyy-MM-dd}", a.Name, today, a.Balance));
        }

        return alerts;
    }

    private static string NameOf(TransactionDto t) => string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName;
}

/// <summary>Budget rollover (#70): what was left unspent last month is added to this month's limit, never below the base limit.</summary>
public static class BudgetRollover
{
    /// <summary>This month's limit including last month's unspent amount (an overspend is not carried forward as a penalty).</summary>
    public static decimal EffectiveLimit(decimal monthlyLimit, decimal previousMonthSpent, bool rollover)
        => rollover ? monthlyLimit + Math.Max(0m, monthlyLimit - previousMonthSpent) : monthlyLimit;
}
