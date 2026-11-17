namespace FlowLedger.Application.Features.Alerts;

public sealed record BudgetUsage(string Category, decimal Limit, decimal Spent);

public sealed record GoalProgress(string Name, decimal Target, decimal Saved, DateOnly? TargetDate);

public sealed record Alert(string Severity, string Title, string Detail);

/// <summary>Pure rules that turn budget and goal state into user-facing alerts (no I/O).</summary>
public static class AlertBuilder
{
    public const decimal WarnAtPercent = 80m;

    public static IReadOnlyList<Alert> Build(IEnumerable<BudgetUsage> budgets, IEnumerable<GoalProgress> goals, DateOnly today)
    {
        var alerts = new List<Alert>();

        foreach (var b in budgets.Where(b => b.Limit > 0))
        {
            var pct = b.Spent / b.Limit * 100m;
            if (b.Spent > b.Limit)
            {
                alerts.Add(new Alert("error", $"{b.Category} is over budget",
                    $"Spent {b.Spent:C0} of {b.Limit:C0} this month ({b.Spent - b.Limit:C0} over)."));
            }
            else if (pct >= WarnAtPercent)
            {
                alerts.Add(new Alert("warning", $"{b.Category} budget is {pct:0}% used",
                    $"{b.Limit - b.Spent:C0} left this month."));
            }
        }

        foreach (var g in goals.Where(g => g.Saved < g.Target && g.TargetDate is { } d && d < today))
        {
            alerts.Add(new Alert("warning", $"Goal \"{g.Name}\" is past its target date",
                $"{g.Target - g.Saved:C0} still to save."));
        }

        return alerts
            .OrderBy(a => a.Severity == "error" ? 0 : 1)
            .ToList();
    }
}
