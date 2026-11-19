using FlowLedger.Application.Features.Goals;
using FlowLedger.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Savings goals with progress and the monthly amount needed to hit the target date.</summary>
internal static class GoalEndpoints
{
    internal sealed record GoalStatus(
        Guid Id, string Name, decimal TargetAmount, decimal SavedAmount, decimal Remaining, double PercentComplete,
        DateOnly? TargetDate, decimal? MonthlyNeeded, bool Complete);

    internal static IEndpointRouteBuilder MapGoalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/goals").WithTags("Goals").RequireRateLimiting("api");

        group.MapGet("/", async (IGoalStore store, ITenantContext tenant, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var goals = (await store.ListAsync(tenant.TenantId, ct)).Select(g =>
            {
                var remaining = Math.Max(0, g.TargetAmount - g.SavedAmount);
                return new GoalStatus(g.Id, g.Name, g.TargetAmount, g.SavedAmount, remaining,
                    (double)Math.Min(100m, g.SavedAmount / g.TargetAmount * 100m), g.TargetDate,
                    GoalMath.MonthlyNeeded(remaining, g.TargetDate, today), remaining == 0);
            }).ToList();
            return Results.Ok(goals);
        })
        .WithName("ListGoals")
        .WithSummary("Savings goals with progress");

        group.MapPut("/", async ([FromBody] SaveGoalRequest r, IValidator<SaveGoalRequest> validator, IGoalStore store, ITenantContext tenant, CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(r, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            try
            {
                var id = await store.UpsertAsync(tenant.TenantId, r.Id, r.Name.Trim(), r.TargetAmount, r.SavedAmount, r.TargetDate, ct);
                return Results.Ok(new { id });
            }
            catch (InvalidOperationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["goal"] = [ex.Message] });
            }
        })
        .WithName("SaveGoal")
        .WithSummary("Create or update a savings goal")
        .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, IGoalStore store, ITenantContext tenant, CancellationToken ct) =>
        {
            await store.RemoveAsync(tenant.TenantId, id, ct);
            return Results.NoContent();
        })
        .WithName("DeleteGoal")
        .WithSummary("Remove a savings goal")
        .RequireRateLimiting("write");

        return app;
    }
}
