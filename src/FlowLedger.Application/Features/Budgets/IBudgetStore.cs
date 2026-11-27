namespace FlowLedger.Application.Features.Budgets;

/// <summary>
/// A monthly limit for one category path (e.g. "Food/Groceries"). With <paramref name="Rollover"/> on, last month's unspent amount is added to
/// the month's limit; <paramref name="CarriedAmount"/> is that amount as worked out for <paramref name="CarriedForMonth"/> (<c>yyyy-MM</c>).
/// </summary>
public sealed record BudgetRow(string Category, decimal MonthlyLimit, uint Version = 0, bool Rollover = false, decimal CarriedAmount = 0m, string? CarriedForMonth = null);

/// <summary>Port for per-tenant monthly category budgets.</summary>
public interface IBudgetStore
{
    Task<IReadOnlyList<BudgetRow>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a budget and returns its new version. With <paramref name="expectedVersion"/> the write only applies if the
    /// row is still at that version, otherwise <c>ConcurrencyConflictException</c>; null keeps last-write-wins. A null
    /// <paramref name="rollover"/> leaves the rollover setting as it is (false for a new budget).
    /// </summary>
    Task<uint> SetAsync(Guid tenantId, string category, decimal monthlyLimit, uint? expectedVersion = null, CancellationToken ct = default, bool? rollover = null);

    /// <summary>Records the amount carried into <paramref name="month"/> (<c>yyyy-MM</c>) and returns the row's new version. Returns 0 when the budget is gone.</summary>
    Task<uint> SetCarriedAsync(Guid tenantId, string category, decimal carriedAmount, string month, CancellationToken ct = default);

    Task RemoveAsync(Guid tenantId, string category, uint? expectedVersion = null, CancellationToken ct = default);
}
