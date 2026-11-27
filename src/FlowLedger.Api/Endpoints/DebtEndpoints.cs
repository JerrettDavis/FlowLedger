using FlowLedger.Application.Features.Debt;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Stateless debt payoff planner: compares avalanche and snowball for the supplied debts.</summary>
internal static class DebtEndpoints
{
    internal sealed record PlanRequest(List<DebtInput> Debts, decimal ExtraMonthly);

    internal sealed record PlanResponse(PayoffPlan Avalanche, PayoffPlan Snowball);

    internal static IEndpointRouteBuilder MapDebtEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/debt").WithTags("Debt").RequireRateLimiting("api");

        group.MapPost("/plan", ([FromBody] PlanRequest r) =>
        {
            if (r.Debts is null || r.Debts.Count is 0 or > 50 || r.ExtraMonthly < 0 || r.ExtraMonthly > ApiLimits.SimulationMonthlyCeiling
                || r.Debts.Any(d => string.IsNullOrWhiteSpace(d.Name) || d.Balance < 0 || d.Balance > ApiLimits.SimulationAmountCeiling
                    || d.AnnualRatePercent < 0 || d.AnnualRatePercent > 100m || d.MinimumPayment < 0 || d.MinimumPayment > ApiLimits.SimulationAnnualCeiling))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["debts"] = ["Provide 1-50 debts with non-negative balance, a 0-100% rate and a non-negative minimum payment."] });
            }

            return Results.Ok(new PlanResponse(
                DebtPayoffPlanner.Plan(r.Debts, r.ExtraMonthly, DebtPayoffPlanner.Avalanche),
                DebtPayoffPlanner.Plan(r.Debts, r.ExtraMonthly, DebtPayoffPlanner.Snowball)));
        })
        .WithName("PlanDebtPayoff")
        .WithSummary("Compare avalanche vs snowball payoff for a set of debts");

        return app;
    }
}
