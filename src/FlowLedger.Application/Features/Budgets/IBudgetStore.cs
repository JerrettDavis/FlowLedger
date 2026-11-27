namespace FlowLedger.Application.Features.Budgets;

/// <summary>A monthly limit for one category path (e.g. "Food/Groceries").</summary>
public sealed record BudgetRow(string Category, decimal MonthlyLimit, uint Version = 0);

/// <summary>Port for per-tenant monthly category budgets.</summary>
public interface IBudgetStore
{
    Task<IReadOnlyList<BudgetRow>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a budget and returns its new version. With <paramref name="expectedVersion"/> the write only applies if the
    /// row is still at that version, otherwise <c>ConcurrencyConflictException</c>; null keeps last-write-wins.
    /// </summary>
    Task<uint> SetAsync(Guid tenantId, string category, decimal monthlyLimit, uint? expectedVersion = null, CancellationToken ct = default);

    Task RemoveAsync(Guid tenantId, string category, uint? expectedVersion = null, CancellationToken ct = default);
}
