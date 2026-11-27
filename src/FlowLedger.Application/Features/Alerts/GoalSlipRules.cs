namespace FlowLedger.Application.Features.Alerts;

/// <summary>A savings goal as the slip rule sees it.</summary>
internal sealed record GoalPace(Guid Id, string Name, decimal Target, decimal Saved, DateOnly? TargetDate);

/// <summary>
/// Pure rule for "this goal will not make its target date at the current pace" (#70). Goals are tracked by hand, so there is no
/// per-goal saving history; the pace is the household's monthly surplus (money in minus money out, transfers excluded) over the last
/// <see cref="SurplusMonths"/> months, shared equally by every goal that is not yet funded. A goal already past its date is left to the
/// existing "past its target date" alert.
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
        if (monthlySurplus is not { } surplus)
        {
            return [];
        }

        var unfunded = goals.Where(g => g.Saved < g.Target).ToList();
        if (unfunded.Count == 0)
        {
            return [];
        }

        var share = surplus / unfunded.Count;
        var alerts = new List<ActivityAlert>();
        foreach (var g in unfunded.Where(g => g.TargetDate is { } d && d > today))
        {
            var target = g.TargetDate!.Value;
            var remaining = g.Target - g.Saved;
            DateOnly? projected = share > 0 ? today.AddMonths((int)Math.Min(1200m, Math.Ceiling(remaining / share))) : null;
            if (projected is { } p && p <= target)
            {
                continue;
            }

            var detail = projected is { } late
                ? $"At the current pace this goal finishes around {late:MMM yyyy}, after its {target:MMM d, yyyy} target."
                : $"At the current pace this goal will not finish by its {target:MMM d, yyyy} target.";
            alerts.Add(new ActivityAlert(ActivityAlertKinds.GoalSlipping, "warning", "Goal behind schedule",
                $"\"{g.Name}\": {detail} {remaining:C0} still to save.", $"{ActivityAlertKinds.GoalSlipping}:{g.Id}:{target:yyyy-MM-dd}",
                g.Name, target, remaining));
        }

        return alerts;
    }
}
