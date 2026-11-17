using FlowLedger.Application.Features.Alerts;
using FlowLedger.Application.Features.Query;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.SharedKernel;

namespace FlowLedger.Api.Endpoints;

/// <summary>Computed alerts: budgets at/over their limit and goals past their target date.</summary>
internal static class AlertEndpoints
{
    internal static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts").WithTags("Alerts").RequireRateLimiting("api");

        group.MapGet("/", async (NpgsqlBudgetStore budgets, NpgsqlGoalStore goals, RunQueryHandler query, ITenantContext tenant, CancellationToken ct) =>
        {
            var budgetRows = await budgets.ListAsync(tenant.TenantId, ct);
            var spend = await query.HandleAsync(
                "transactions | where direction == \"Debit\" and is_transfer == false and date >= startofmonth(today()) " +
                "| summarize total = sum(amount) by category", ct);
            var spent = spend.Rows.ToDictionary(
                r => r[0]?.ToString() ?? "", r => r[1] is decimal d ? d : Convert.ToDecimal(r[1] ?? 0m), StringComparer.OrdinalIgnoreCase);

            var usage = budgetRows.Select(b => new BudgetUsage(b.Category, b.MonthlyLimit,
                spent.Where(kv => kv.Key.Equals(b.Category, StringComparison.OrdinalIgnoreCase)
                    || kv.Key.StartsWith(b.Category + "/", StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value)));
            var progress = (await goals.ListAsync(tenant.TenantId, ct)).Select(g => new GoalProgress(g.Name, g.TargetAmount, g.SavedAmount, g.TargetDate));

            return Results.Ok(AlertBuilder.Build(usage, progress, DateOnly.FromDateTime(DateTime.UtcNow)));
        })
        .WithName("ListAlerts")
        .WithSummary("Budget and goal alerts");

        return app;
    }
}
