namespace FlowLedger.Application.Features.Budgets;

/// <summary>A monthly limit for one category path (e.g. "Food/Groceries").</summary>
public sealed record BudgetRow(string Category, decimal MonthlyLimit);

/// <summary>Port for per-tenant monthly category budgets.</summary>
public interface IBudgetStore
{
    Task<IReadOnlyList<BudgetRow>> ListAsync(Guid tenantId, CancellationToken ct = default);

    Task SetAsync(Guid tenantId, string category, decimal monthlyLimit, CancellationToken ct = default);

    Task RemoveAsync(Guid tenantId, string category, CancellationToken ct = default);
}
