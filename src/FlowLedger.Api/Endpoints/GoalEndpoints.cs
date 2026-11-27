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
        DateOnly? TargetDate, decimal? MonthlyNeeded, bool Complete, uint Version = 0,
        Guid? LinkedAccountId = null, string? LinkedAccountName = null, decimal? CurrentAmount = null, decimal? AccountMonthlyTrend = null);

    internal static IEndpointRouteBuilder MapGoalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/goals").WithTags("Goals").RequireRateLimiting("api");

        group.MapGet("/", async (GetGoalStatusesHandler handler, TimeProvider clock, CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var goals = (await handler.HandleAsync(ct)).Select(g =>
            {
                // Progress is measured on CurrentAmount: the linked account's balance when the goal is linked, else the saved amount.
                var remaining = Math.Max(0, g.TargetAmount - g.CurrentAmount);
                return new GoalStatus(g.Id, g.Name, g.TargetAmount, g.SavedAmount, remaining,
                    (double)Math.Min(100m, g.CurrentAmount / g.TargetAmount * 100m), g.TargetDate,
                    GoalMath.MonthlyNeeded(remaining, g.TargetDate, today), remaining == 0, g.Version,
                    g.LinkedAccountId, g.LinkedAccountName, g.CurrentAmount, g.AccountMonthlyTrend);
            }).ToList();
            return Results.Ok(goals);
        })
        .WithName("ListGoals")
        .WithSummary("Savings goals with progress (a goal linked to an account is measured on that account's balance)");

        group.MapPut("/", async (
            [FromBody] SaveGoalRequest r, IValidator<SaveGoalRequest> validator, SaveGoalHandler handler,
            HttpRequest httpRequest, HttpResponse response, CancellationToken ct) =>
        {
            var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
            if (malformed)
            {
                return RowVersioning.Malformed("goal");
            }

            var validation = await validator.ValidateAsync(r, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var (id, version) = await handler.HandleAsync(r, expected, ct);
            return RowVersioning.Ok(response, new { id, version }, version);
        })
        .WithName("SaveGoal")
        .WithSummary("Create or update a savings goal")
        .RequireRateLimiting("write");

        group.MapDelete("/{id:guid}", async (Guid id, IGoalStore store, ITenantContext tenant, HttpRequest httpRequest, CancellationToken ct) =>
        {
            var expected = RowVersioning.ExpectedVersion(httpRequest, out var malformed);
            if (malformed)
            {
                return RowVersioning.Malformed("goal");
            }

            await store.RemoveAsync(tenant.TenantId, id, expected, ct);
            return Results.NoContent();
        })
        .WithName("DeleteGoal")
        .WithSummary("Remove a savings goal")
        .RequireRateLimiting("write");

        return app;
    }
}
