namespace FlowLedger.Application.Features.Strategies;

/// <summary>
/// A rate the user entered for one account: APY for deposit accounts, APR for loans and cards.
/// <see cref="RemainingMonths"/> is optional and only used for loan refinance maths.
/// </summary>
public sealed record AccountRate(Guid AccountId, decimal RatePercent, int? RemainingMonths);

/// <summary>Port for per-tenant account rates (kept outside the Account aggregate so no schema migration is needed).</summary>
public interface IAccountRateStore
{
    Task<IReadOnlyList<AccountRate>> ListAsync(Guid tenantId, CancellationToken ct = default);

    Task UpsertAsync(Guid tenantId, AccountRate rate, CancellationToken ct = default);

    Task RemoveAsync(Guid tenantId, Guid accountId, CancellationToken ct = default);
}

public static class AccountRateRules
{
    public static bool IsValid(decimal rate, int? remainingMonths) =>
        rate is >= 0 and <= 100 && (remainingMonths is null or (>= 1 and <= 600));
}
