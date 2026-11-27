namespace FlowLedger.Application.Abstractions;

/// <summary>
/// A tenant tried to create more of something than the per-tenant cap allows (#166). The API maps this to HTTP 409 Conflict.
/// Transactions have their own, larger quota (<c>TransactionQuota</c>).
/// </summary>
public sealed class TenantQuotaExceededException(string noun, int limit)
    : Exception($"You have reached the limit of {limit:N0} {noun}. Remove some before adding more.")
{
    public string Noun { get; } = noun;
    public int Limit { get; } = limit;
}

/// <summary>
/// Per-tenant ceilings for the tenant-owned collections that had no cap, so a runaway tenant cannot fill the shared database
/// through saved reports, categories, recurring flows, accounts or assets. Generous for a household; the point is a bound, not a squeeze.
/// </summary>
public static class TenantLimits
{
    public const int MaxAccounts = 200;
    public const int MaxAssets = 1_000;
    public const int MaxCategories = 2_000;
    public const int MaxRecurringFlows = 1_000;
    public const int MaxSavedReports = 200;

    /// <summary>Throws <see cref="TenantQuotaExceededException"/> when <paramref name="current"/> plus <paramref name="adding"/> would pass <paramref name="max"/>.</summary>
    public static void EnsureRoom(int current, int adding, int max, string noun)
    {
        if (current + adding > max)
        {
            throw new TenantQuotaExceededException(noun, max);
        }
    }
}
