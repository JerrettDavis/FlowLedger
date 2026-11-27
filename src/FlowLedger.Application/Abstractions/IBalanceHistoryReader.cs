namespace FlowLedger.Application.Abstractions;

/// <summary>One account's balance as of a day (the last snapshot captured that day).</summary>
/// <summary>One account's balance on one day. <paramref name="Currency"/> is the snapshot's own code, or null when unknown (callers fall back to the account's currency).</summary>
public sealed record BalancePoint(DateOnly Date, Guid AccountId, decimal Balance, string? Currency = null);

/// <summary>Read-only view over the append-only balance snapshots written on every sync.</summary>
public interface IBalanceHistoryReader
{
    /// <summary>How far back <see cref="DailyAccountBalancesAsync(CancellationToken)"/> reads when no window is given.</summary>
    public const int DefaultWindowDays = 400;

    /// <summary>The default window (<see cref="DefaultWindowDays"/> days back from today, UTC), plus each account's anchor point before it.</summary>
    Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(CancellationToken ct = default);

    /// <summary>
    /// Daily points in [<paramref name="from"/>, <paramref name="to"/>] (open-ended when null), plus for every account the latest
    /// point BEFORE <paramref name="from"/> so a caller can still find the balance at the window start without reading older history.
    /// </summary>
    Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(DateOnly from, DateOnly? to = null, CancellationToken ct = default);
}
