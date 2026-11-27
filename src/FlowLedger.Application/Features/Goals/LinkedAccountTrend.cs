using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Goals;

/// <summary>
/// The 90-day balance trend of one account (#70): how much its balance changed per month over the last
/// <see cref="TrendDays"/> days. Balance history is not stored per day, so it is rebuilt from the account's own real
/// transactions (transfers included, because they move the account's balance): balance now minus balance 90 days ago
/// equals the net of the transactions in between. Used as the pace for a goal that is linked to that account.
/// </summary>
internal static class LinkedAccountTrend
{
    /// <summary>The window the trend looks at.</summary>
    public const int TrendDays = 90;

    /// <summary>How far back callers load transactions for the trend (the trend window plus slack). Shared so the goals page and the alerts agree.</summary>
    public const int HistoryDays = 200;

    /// <summary>The window expressed in months, the divisor for the monthly figure.</summary>
    public const int TrendMonths = 3;

    /// <summary>Less history than this on the account and no trend is claimed, so a newly linked account does not produce noise.</summary>
    public const int MinHistoryDays = 60;

    /// <summary>
    /// Average monthly change of the account's balance over the trend window, or null when the account has too little history in
    /// <paramref name="history"/> (transactions of any account; only the given one is used). Negative means the balance is falling.
    /// </summary>
    public static decimal? MonthlyChange(DateOnly today, Guid accountId, IReadOnlyList<TransactionDto> history)
    {
        static DateOnly Day(TransactionDto t) => t.PostedDate ?? t.EffectiveDate;
        var mine = history.Where(t => t.AccountId == accountId && TransactionStatusExtensions.IsRealStatus(t.Status)).ToList();
        if (mine.Count == 0 || today.DayNumber - mine.Min(Day).DayNumber < MinHistoryDays)
        {
            return null;
        }

        var from = today.AddDays(-TrendDays);
        var credit = nameof(TransactionDirection.Credit);
        var net = mine.Where(t => Day(t) > from && Day(t) <= today).Sum(t => t.Direction == credit ? t.Amount : -t.Amount);
        return net / TrendMonths;
    }
}
