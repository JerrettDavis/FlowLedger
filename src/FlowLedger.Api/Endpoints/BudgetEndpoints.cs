using FlowLedger.Application.Features.Budgets;
using FlowLedger.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Monthly category budgets with this month's actual spending (transfers excluded).</summary>
internal static class BudgetEndpoints
{
    internal static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/budgets").WithTags("Budgets").RequireRateLimiting("api");

        group.MapGet("/", async (ListBudgetsHandler handler, string? month, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(
                month is not null && DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out var m) ? m : null, ct)))
        .WithName("ListBudgets")
        .WithSummary("Budgets with this month's spending");

        group.MapPut("/", async (
            [FromBody] SetBudgetRequest request, IValidator<SetBudgetRequest> validator, IBudgetStore store, ITenantContext tenant, CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            try
            {
                await store.SetAsync(tenant.TenantId, request.Category.Trim(), request.MonthlyLimit, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["budget"] = [ex.Message] });
            }
        })
        .WithName("SetBudget")
        .WithSummary("Create or update a monthly budget for a category")
        .RequireRateLimiting("write");

        group.MapDelete("/", async ([FromQuery] string category, IBudgetStore store, ITenantContext tenant, CancellationToken ct) =>
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
