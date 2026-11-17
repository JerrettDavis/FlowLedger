namespace FlowLedger.Application.Features.Goals;

public static class GoalMath
{
    /// <summary>Monthly amount to save to reach the target by the target date; null when done or undated. Past dates need it all now.</summary>
    public static decimal? MonthlyNeeded(decimal remaining, DateOnly? targetDate, DateOnly today)
    {
        if (remaining <= 0 || targetDate is not { } d)
        {
            return null;
        }

        var months = (d.Year - today.Year) * 12 + d.Month - today.Month;
        return Math.Round(remaining / Math.Max(1, months), 2);
    }
}
