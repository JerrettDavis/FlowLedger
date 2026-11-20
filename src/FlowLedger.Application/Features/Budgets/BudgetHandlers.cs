using FlowLedger.SharedKernel;
using FluentValidation;

namespace FlowLedger.Application.Features.Budgets;

public sealed record SetBudgetRequest(string Category, decimal MonthlyLimit);

public sealed record BudgetStatus(string Category, decimal MonthlyLimit, decimal Spent, decimal Remaining, double PercentUsed, bool Over);

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
public sealed class ListBudgetsHandler(IBudgetStore store, GetMonthlySpendByCategoryHandler spend, ITenantContext tenant)
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
            return new BudgetStatus(b.Category, b.MonthlyLimit, spent, b.MonthlyLimit - spent, pct, spent > b.MonthlyLimit);
        }).ToList();

        return new BudgetOverview((month ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM"), rows, spentByCategory);
    }
}
