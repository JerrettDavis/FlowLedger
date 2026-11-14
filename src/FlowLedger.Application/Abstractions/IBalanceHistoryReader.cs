namespace FlowLedger.Application.Abstractions;

/// <summary>One account's balance as of a day (the last snapshot captured that day).</summary>
public sealed record BalancePoint(DateOnly Date, Guid AccountId, decimal Balance);

/// <summary>Read-only view over the append-only balance snapshots written on every sync.</summary>
public interface IBalanceHistoryReader
{
    Task<IReadOnlyList<BalancePoint>> DailyAccountBalancesAsync(CancellationToken ct = default);
}
