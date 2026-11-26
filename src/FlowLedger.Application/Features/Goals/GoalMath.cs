namespace FlowLedger.Application.Features.Goals;

public static class GoalMath
{
    /// <summary>Whole calendar months from <paramref name="today"/> to <paramref name="target"/>, at least 1. The single month-difference helper used by goals and the forecast.</summary>
    public static int MonthsUntil(DateOnly today, DateOnly target)
        => Math.Max(1, (target.Year - today.Year) * 12 + target.Month - today.Month);

    /// <summary>Monthly amount to save to reach the target by the target date; null when done or undated. Past dates need it all now.</summary>
    public static decimal? MonthlyNeeded(decimal remaining, DateOnly? targetDate, DateOnly today)
    {
        if (remaining <= 0 || targetDate is not { } d)
        {
            return null;
        }

        return FlowLedger.Domain.ValueObjects.MoneyRounding.Round(remaining / MonthsUntil(today, d));
    }
}
