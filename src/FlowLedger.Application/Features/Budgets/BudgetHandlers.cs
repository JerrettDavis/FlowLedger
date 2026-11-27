using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using FluentValidation;

namespace FlowLedger.Application.Features.Budgets;

public sealed record SetBudgetRequest(string Category, decimal MonthlyLimit);

public sealed record BudgetStatus(string Category, decimal MonthlyLimit, decimal Spent, decimal Remaining, double PercentUsed, bool Over, uint Version = 0);

public sealed record BudgetOverview(string Month, IReadOnlyList<BudgetStatus> Budgets, Dictionary<string, decimal> SpendByCategory);

public sealed class SetBudgetRequestValidator : AbstractValidator<SetBudgetRequest>
{
    public SetBudgetRequestValidator()
    {
        RuleFor(x => x)
            .Must(r => !string.IsNullOrWhiteSpace(r.Category) && r.MonthlyLimit >= 0 && r.MonthlyLimit <= 100_000_000m)
            .WithMessage("A category and a non-negative limit are required.")
            .OverridePropertyName("budget");
    }
}

/// <summary>Budgets with this month's actual spending (transfers excluded), rolled up from child categories.</summary>
public sealed class ListBudgetsHandler(IBudgetStore store, GetMonthlySpendByCategoryHandler spend, ITenantContext tenant, TimeProvider clock)
{
    public async Task<BudgetOverview> HandleAsync(CancellationToken ct = default) => await HandleAsync(null, ct);

    /// <summary>Budgets against the spending of <paramref name="month"/> (null = the current month).</summary>
    public async Task<BudgetOverview> HandleAsync(DateOnly? month, CancellationToken ct = default)
    {
        var budgets = await store.ListAsync(tenant.TenantId, ct);
        var spentByCategory = await spend.HandleAsync(month, ct);

        var rows = budgets.Select(b =>
        {
            var spent = MonthlySpend.SpentFor(b.Category, spentByCategory);
            var pct = b.MonthlyLimit > 0 ? (double)(spent / b.MonthlyLimit * 100m) : (spent > 0 ? 100d : 0d);
            return new BudgetStatus(b.Category, b.MonthlyLimit, spent, b.MonthlyLimit - spent, pct, spent > b.MonthlyLimit, b.Version);
        }).ToList();

        return new BudgetOverview((month ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)).ToString("yyyy-MM", CultureInfo.InvariantCulture), rows, spentByCategory);
    }
}

/// <summary>The per-tenant budget quota: a business limit, so it lives here and not in the store (#121).</summary>
public static class BudgetLimits
{
    public const int MaxBudgets = 500;
}

/// <summary>Creates or updates one budget, refusing a new one past <see cref="BudgetLimits.MaxBudgets"/>; updating an existing one is always allowed.</summary>
public sealed class SetBudgetHandler(IBudgetStore store, ITenantContext tenant)
{
    public async Task<uint> HandleAsync(string category, decimal monthlyLimit, uint? expectedVersion = null, CancellationToken ct = default)
    {
        var existing = await store.ListAsync(tenant.TenantId, ct);
        if (existing.Count >= BudgetLimits.MaxBudgets && !existing.Any(b => string.Equals(b.Category, category, StringComparison.Ordinal)))
        {
            throw new RequestValidationException("budget", $"You can have at most {BudgetLimits.MaxBudgets} budgets. Remove one before adding another.");
        }

        return await store.SetAsync(tenant.TenantId, category, monthlyLimit, expectedVersion, ct);
    }
}
