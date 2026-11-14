using FlowLedger.Application.Features.Query;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Monthly category budgets with this month's actual spending (transfers excluded).</summary>
internal static class BudgetEndpoints
{
    internal sealed record SetBudgetRequest(string Category, decimal MonthlyLimit);

    internal sealed record BudgetStatus(string Category, decimal MonthlyLimit, decimal Spent, decimal Remaining, double PercentUsed, bool Over);

    internal static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/budgets").WithTags("Budgets").RequireRateLimiting("api");

        group.MapGet("/", async (NpgsqlBudgetStore store, RunQueryHandler query, ITenantContext tenant, CancellationToken ct) =>
        {
            var budgets = await store.ListAsync(tenant.TenantId, ct);
            var spend = await query.HandleAsync(
                "transactions | where direction == \"Debit\" and is_transfer == false and date >= startofmonth(today()) " +
                "| summarize total = sum(amount) by category", ct);
            var spentByCategory = spend.Rows.ToDictionary(
                r => r[0]?.ToString() ?? "", r => r[1] is decimal d ? d : Convert.ToDecimal(r[1] ?? 0m), StringComparer.OrdinalIgnoreCase);

            var rows = budgets.Select(b =>
            {
                // A budget on a parent ("Food") covers its children ("Food/Groceries").
                var spent = spentByCategory.Where(kv => kv.Key.Equals(b.Category, StringComparison.OrdinalIgnoreCase)
                        || kv.Key.StartsWith(b.Category + "/", StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
                var pct = b.MonthlyLimit > 0 ? (double)(spent / b.MonthlyLimit * 100m) : (spent > 0 ? 100d : 0d);
                return new BudgetStatus(b.Category, b.MonthlyLimit, spent, b.MonthlyLimit - spent, pct, spent > b.MonthlyLimit);
            }).ToList();

            return Results.Ok(new { month = DateTime.UtcNow.ToString("yyyy-MM"), budgets = rows, spendByCategory = spentByCategory });
        })
        .WithName("ListBudgets")
        .WithSummary("Budgets with this month's spending");

        group.MapPut("/", async ([FromBody] SetBudgetRequest request, NpgsqlBudgetStore store, ITenantContext tenant, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Category) || request.MonthlyLimit < 0 || request.MonthlyLimit > 100_000_000m)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["budget"] = ["A category and a non-negative limit are required."] });
            }

            await store.SetAsync(tenant.TenantId, request.Category.Trim(), request.MonthlyLimit, ct);
            return Results.NoContent();
        })
        .WithName("SetBudget")
        .WithSummary("Create or update a monthly budget for a category")
        .RequireRateLimiting("write");

        group.MapDelete("/", async ([FromQuery] string category, NpgsqlBudgetStore store, ITenantContext tenant, CancellationToken ct) =>
        {
            await store.RemoveAsync(tenant.TenantId, category, ct);
            return Results.NoContent();
        })
        .WithName("DeleteBudget")
        .WithSummary("Remove a budget (never touches transactions)")
        .RequireRateLimiting("write");

        return app;
    }
}
