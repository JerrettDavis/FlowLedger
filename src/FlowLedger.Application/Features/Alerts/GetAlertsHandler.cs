using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Goals;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Alerts;

/// <summary>Computed alerts: budgets at/over their limit and goals past their target date.</summary>
public sealed class GetAlertsHandler(IBudgetStore budgets, IGoalStore goals, GetMonthlySpendByCategoryHandler spend, ITenantContext tenant, TimeProvider? clock = null)
{
    public async Task<IReadOnlyList<Alert>> HandleAsync(CancellationToken ct = default)
    {
        var budgetRows = await budgets.ListAsync(tenant.TenantId, ct);
        var spent = await spend.HandleAsync(ct);

        var usage = budgetRows.Select(b => new BudgetUsage(b.Category, b.MonthlyLimit, MonthlySpend.SpentFor(b.Category, spent)));
        var progress = (await goals.ListAsync(tenant.TenantId, ct)).Select(g => new GoalProgress(g.Name, g.TargetAmount, g.SavedAmount, g.TargetDate));

        return AlertBuilder.Build(usage, progress, DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime));
    }
}
