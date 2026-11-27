using FlowLedger.Application.Features.Goals;

namespace FlowLedger.Application.Features.Alerts;

/// <summary>The account a goal is linked to, as the goal rules see it (#70).</summary>
/// <param name="AccountId">The linked account.</param>
/// <param name="AccountName">Its display name.</param>
/// <param name="Balance">Its current balance, which is the goal's progress.</param>
/// <param name="MonthlyTrend">Its 90-day balance change per month, or null when it has too little history to say.</param>
internal sealed record LinkedAccountPace(Guid AccountId, string AccountName, decimal Balance, decimal? MonthlyTrend);

/// <summary>A savings goal as the slip rule sees it. <paramref name="Saved"/> is already the effective amount: the linked account's balance when linked.</summary>
internal sealed record GoalPace(Guid Id, string Name, decimal Target, decimal Saved, DateOnly? TargetDate, LinkedAccountPace? Linked = null);

/// <summary>Builds the <see cref="GoalPace"/> list: a goal linked to a known account takes that account's balance as its progress and its trend as its pace.</summary>
internal static class GoalPaces
{
    public static IReadOnlyList<GoalPace> Resolve(
        DateOnly today, IReadOnlyList<GoalRow> goals, IReadOnlyList<BalanceInput> balances, IReadOnlyList<TransactionDto> history)
    {
        var byId = balances.ToDictionary(b => b.Id);
        return goals.Select(g =>
        {
            if (g.LinkedAccountId is not { } accountId || !byId.TryGetValue(accountId, out var account))
            {
                // Unlinked, or the linked account is gone or inactive: the goal keeps its hand-entered progress and the household pace.
                return new GoalPace(g.Id, g.Name, g.TargetAmount, g.SavedAmount, g.TargetDate);
            }

            var linked = new LinkedAccountPace(accountId, account.Name, account.Balance, LinkedAccountTrend.MonthlyChange(today, accountId, history));
            return new GoalPace(g.Id, g.Name, g.TargetAmount, Math.Max(0m, account.Balance), g.TargetDate, linked);
        }).ToList();
    }

    /// <summary>The names of the not-yet-funded goals linked to each account, for the low-balance alert on that account.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<string>> UnfinishedByAccount(IReadOnlyList<GoalPace> paces)
        => paces
            .Where(p => p.Linked is not null && p.Saved < p.Target)
            .GroupBy(p => p.Linked!.AccountId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
}

/// <summary>
/// Pure rule for "this goal will not make its target date at the current pace" (#70). Two pace models:
/// <list type="number">
/// <item><description><b>Linked goal</b> (the goal has an account): progress is that account's balance and the pace is that account's own
/// 90-day balance trend per month (<see cref="LinkedAccountTrend"/>), used in full: the account is the goal's, so it is not shared.</description></item>
/// <item><description><b>Household fallback</b> (no account, or the account has too little history for a trend): goals are tracked by hand, so
/// there is no per-goal saving history; the pace is the household's monthly surplus (money in minus money out, transfers excluded) over the
/// last <see cref="SurplusMonths"/> months, shared equally by every goal that uses this model and is not yet funded.</description></item>
/// </list>
/// A goal already past its date is left to the existing "past its target date" alert.
/// </summary>
internal static class GoalSlipRules
{
    /// <summary>How many whole months of history the surplus is averaged over.</summary>
    public const int SurplusMonths = 3;

    /// <summary>Less history than this and no pace is claimed, so a brand new household is not alerted from noise.</summary>
    public const int MinHistoryDays = 60;

    /// <summary>
    /// Average monthly surplus over the last <see cref="SurplusMonths"/> months, or null when the history is too short to say.
    /// </summary>
    public static decimal? MonthlySurplus(DateOnly today, IReadOnlyList<TransactionDto> history, IReadOnlySet<Guid> transferIds)
    {
        var real = history
            .Where(t => FlowLedger.Domain.Aggregates.TransactionStatusExtensions.IsRealStatus(t.Status) && !transferIds.Contains(t.Id))
            .ToList();
        static DateOnly Day(TransactionDto t) => t.PostedDate ?? t.EffectiveDate;
        if (real.Count == 0 || today.DayNumber - real.Min(Day).DayNumber < MinHistoryDays)
        {
            return null;
        }

        var from = today.AddMonths(-SurplusMonths);
        var window = real.Where(t => Day(t) > from && Day(t) <= today).ToList();
        var credit = nameof(FlowLedger.Domain.Aggregates.TransactionDirection.Credit);
        var net = window.Sum(t => t.Direction == credit ? t.Amount : -t.Amount);
        return net / SurplusMonths;
    }

    public static IReadOnlyList<ActivityAlert> Build(DateOnly today, IReadOnlyList<GoalPace> goals, decimal? monthlySurplus)
    {
        var unfunded = goals.Where(g => g.Saved < g.Target).ToList();
        if (unfunded.Count == 0)
        {
            return [];
        }

        // Goals with their own trend do not draw on the household surplus; everything else splits it equally.
        var pooled = unfunded.Count(g => g.Linked?.MonthlyTrend is null);
        decimal? share = monthlySurplus is { } surplus && pooled > 0 ? surplus / pooled : null;
        var alerts = new List<ActivityAlert>();
        foreach (var g in unfunded.Where(g => g.TargetDate is { } d && d > today))
        {
            var ownTrend = g.Linked?.MonthlyTrend;
            var pace = ownTrend ?? share;
            if (pace is not { } rate)
            {
                continue; // no pace can be claimed for this goal
            }

            var target = g.TargetDate!.Value;
            var remaining = g.Target - g.Saved;
            DateOnly? projected = rate > 0 ? today.AddMonths((int)Math.Min(1200m, Math.Ceiling(remaining / rate))) : null;
            if (projected is { } p && p <= target)
            {
                continue;
            }

            var basis = ownTrend is null ? "the current pace" : $"{g.Linked!.AccountName}'s 90-day trend ({rate:C0} a month)";
            var detail = projected is { } late
                ? $"At {basis} this goal finishes around {late:MMM yyyy}, after its {target:MMM d, yyyy} target."
                : $"At {basis} this goal will not finish by its {target:MMM d, yyyy} target.";
            alerts.Add(new ActivityAlert(ActivityAlertKinds.GoalSlipping, "warning", "Goal behind schedule",
                $"\"{g.Name}\": {detail} {remaining:C0} still to save.", $"{ActivityAlertKinds.GoalSlipping}:{g.Id}:{target:yyyy-MM-dd}",
                g.Name, target, remaining));
        }

        return alerts;
    }
}
