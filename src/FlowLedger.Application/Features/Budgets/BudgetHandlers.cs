using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using FluentValidation;

namespace FlowLedger.Application.Features.Budgets;

public sealed record SetBudgetRequest(string Category, decimal MonthlyLimit, bool? Rollover = null);

/// <param name="MonthlyLimit">The limit the user set; with rollover on, spending is judged against <paramref name="MonthlyLimit"/> plus <paramref name="Carried"/>.</param>
/// <param name="Carried">Last month's unspent amount carried into this month (zero without rollover).</param>
public sealed record BudgetStatus(string Category, decimal MonthlyLimit, decimal Spent, decimal Remaining, double PercentUsed, bool Over, uint Version = 0, bool Rollover = false, decimal Carried = 0m);

public sealed record BudgetOverview(string Month, IReadOnlyList<BudgetStatus> Budgets, Dictionary<string, decimal> SpendByCategory);

internal sealed class SetBudgetRequestValidator : AbstractValidator<SetBudgetRequest>
{
    public SetBudgetRequestValidator()
    {
        RuleFor(x => x.Category).NotEmpty().WithMessage("Pick a category for the budget.");
        RuleFor(x => x.MonthlyLimit).InclusiveBetween(0m, 100_000_000m).WithMessage("The monthly limit must be between 0 and 100,000,000.");
    }
}

/// <summary>Budgets with this month's actual spending (transfers excluded), rolled up from child categories.</summary>
internal sealed class ListBudgetsHandler(EffectiveBudgetService budgets, GetMonthlySpendByCategoryHandler spend, TimeProvider clock)
{
    public async Task<BudgetOverview> HandleAsync(CancellationToken ct = default) => await HandleAsync(null, ct);

    /// <summary>Budgets against the spending of <paramref name="month"/> (null = the current month).</summary>
    public async Task<BudgetOverview> HandleAsync(DateOnly? month, CancellationToken ct = default)
    {
        var effective = await budgets.ListAsync(month, ct);
        var spentByCategory = await spend.HandleAsync(month, ct);

        var rows = effective.Select(b =>
        {
            var spent = MonthlySpend.SpentFor(b.Row.Category, spentByCategory);
            var pct = b.EffectiveLimit > 0 ? (double)(spent / b.EffectiveLimit * 100m) : (spent > 0 ? 100d : 0d);
            return new BudgetStatus(
                b.Row.Category, b.Row.MonthlyLimit, spent, b.EffectiveLimit - spent, pct, spent > b.EffectiveLimit, b.Version, b.Row.Rollover, b.Carried);
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
internal sealed class SetBudgetHandler(IBudgetStore store, ITenantContext tenant)
{
    public async Task<uint> HandleAsync(string category, decimal monthlyLimit, uint? expectedVersion = null, CancellationToken ct = default, bool? rollover = null)
    {
        var existing = await store.ListAsync(tenant.TenantId, ct);
        if (existing.Count >= BudgetLimits.MaxBudgets && !existing.Any(b => string.Equals(b.Category, category, StringComparison.Ordinal)))
        {
            throw new RequestValidationException("budget", $"You can have at most {BudgetLimits.MaxBudgets} budgets. Remove one before adding another.");
        }

        return await store.SetAsync(tenant.TenantId, category, monthlyLimit, expectedVersion, ct, rollover);
    }
}
