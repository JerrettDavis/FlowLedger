using FlowLedger.Application.Features.Strategies;
using Microsoft.AspNetCore.Mvc;

namespace FlowLedger.Api.Endpoints;

/// <summary>Strategy suggestions (cash yield, CD ladders, debt, refinance, budget) plus what-if simulators.</summary>
internal static class StrategyEndpoints
{
    internal sealed record GrowthRequest(decimal Start, decimal BaseMonthly, decimal BaseApy, decimal ScenarioMonthly, decimal ScenarioApy, int Years);

    internal sealed record RefinanceRequest(decimal Balance, decimal CurrentRate, int RemainingMonths, decimal NewRate, int NewTermMonths, decimal ClosingCosts);

    internal sealed record LadderRequest(decimal Amount);

    internal sealed record FireRequest(decimal Invested, decimal MonthlyContribution, decimal AnnualSpend, decimal WithdrawalRate, decimal RealReturn, decimal? Age, int Years);

    internal sealed record AccountRateRequest(decimal RatePercent, int? RemainingMonths);

    internal static IEndpointRouteBuilder MapStrategyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/strategies").WithTags("Strategies").RequireRateLimiting("api");

        group.MapGet("", async (StrategyService svc, CancellationToken ct) => Results.Ok(await svc.GetReportAsync(ct)))
            .WithName("GetStrategies")
            .WithSummary("Ranked, explainable suggestions derived from balances, cash flow and current market rates");

        group.MapGet("/rates", async (StrategyService svc, CancellationToken ct) => Results.Ok(await svc.GetRatesAsync(ct)))
            .WithName("GetMarketRates")
            .WithSummary("Current benchmark rates (mortgage, auto, credit card, personal loan, savings, CDs)");

        group.MapGet("/account-rates", async (StrategyService svc, CancellationToken ct) => Results.Ok(await svc.ListAccountRatesAsync(ct)))
            .WithName("ListAccountRates")
            .WithSummary("Your entered APY/APR per account");

        group.MapPut("/account-rates/{accountId:guid}", async (Guid accountId, [FromBody] AccountRateRequest r, StrategyService svc, CancellationToken ct) =>
            {
                if (!AccountRateRules.IsValid(r.RatePercent, r.RemainingMonths))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["rate"] = ["Rate must be 0-100% and months remaining 1-600."] });
                }

                return await svc.SaveAccountRateAsync(new AccountRate(accountId, r.RatePercent, r.RemainingMonths), ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            })
            .WithName("SaveAccountRate")
            .WithSummary("Record the APY (deposit) or APR (loan/card) of an account for exact suggestions");

        group.MapDelete("/account-rates/{accountId:guid}", async (Guid accountId, StrategyService svc, CancellationToken ct) =>
            {
                await svc.RemoveAccountRateAsync(accountId, ct);
                return Results.NoContent();
            })
            .WithName("RemoveAccountRate")
            .WithSummary("Forget an account's entered rate");

        group.MapPost("/simulate/growth", ([FromBody] GrowthRequest r) =>
            {
                if (r.Start < 0 || r.Start > 1_000_000_000m || r.BaseMonthly is < -1_000_000m or > 1_000_000m || r.ScenarioMonthly is < -1_000_000m or > 1_000_000m
                    || r.BaseApy is < -50 or > 100 || r.ScenarioApy is < -50 or > 100 || r.Years is < 1 or > 50)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["growth"] = ["Use a non-negative start, sane monthly amounts, rates between -50% and 100%, and 1-50 years."] });
                }

                return Results.Ok(Simulators.CompareGrowth(r.Start, r.BaseMonthly, r.BaseApy, r.ScenarioMonthly, r.ScenarioApy, r.Years * 12));
            })
            .WithName("SimulateGrowth")
            .WithSummary("Compare two forward growth paths (contribution and rate)");

        group.MapPost("/simulate/fire", ([FromBody] FireRequest r) =>
            {
                if (r.Invested is < 0 or > 1_000_000_000m || r.MonthlyContribution is < -1_000_000m or > 1_000_000m || r.AnnualSpend is <= 0 or > 100_000_000m
                    || r.WithdrawalRate is <= 0 or > 20 || r.RealReturn is < -50 or > 50 || r.Age is < 0 or > 120 || r.Years is < 1 or > FirePlanner.MaxYears)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["fire"] = ["Use positive spending, a 0-20% withdrawal rate, a sane return, and 1-60 years."] });
                }

                return Results.Ok(FirePlanner.Plan(r.Invested, r.MonthlyContribution, r.AnnualSpend, r.WithdrawalRate, r.RealReturn, r.Age, r.Years));
            })
            .WithName("SimulateFire")
            .WithSummary("Years until invested assets can fund your spending at a safe withdrawal rate");

        group.MapPost("/simulate/past", async (StrategyService svc, [FromBody] PastWhatIfRequest r, CancellationToken ct) =>
            {
                if (r.Months is < 1 or > 60 || r.ExtraMonthly is < 0 or > 1_000_000m || r.CutPercent is < 0 or > 100 || r.IdleCash is < 0 or > 1_000_000_000m
                    || r.FromApy is < -50 or > 100 || r.ToApy is < -50 or > 100 || r.GrowthApy is < -50 or > 100)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["past"] = ["Months 1-60, non-negative amounts, percentages 0-100, rates -50% to 100%."] });
                }

                return Results.Ok(await svc.PastWhatIfAsync(r, ct));
            })
            .WithName("SimulatePast")
            .WithSummary("Replay your history with a different choice (save more, cut a category, move idle cash)");

        group.MapPost("/simulate/goal", async (StrategyService svc, [FromBody] GoalRequest r, CancellationToken ct) =>
            {
                if (r.Target is <= 0 or > 1_000_000_000m || r.Saved is < 0 or > 1_000_000_000m || r.Monthly is < 0 or > 10_000_000m
                    || r.Apy is < -50 or > 100 || r.ScenarioApy is < -50 or > 100 || r.DeadlineMonths is < 0 or > 600
                    || r.ExtraMonthly is < 0 or > 10_000_000m || r.IncomeRaisePct is < 0 or > 1000 || r.SpendCutPct is < 0 or > 100
                    || r.LumpSum is < 0 or > 1_000_000_000m)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["goal"] = ["Provide a positive target, non-negative amounts, rates -50% to 100%, and a deadline of 0-600 months."] });
                }

                return Results.Ok(await svc.PlanGoalAsync(r, ct));
            })
            .WithName("SimulateGoal")
            .WithSummary("Paths to a savings target: timeline, required deposit and ranked levers (savings, income, debt, yield)");

        group.MapPost("/simulate/refinance", ([FromBody] RefinanceRequest r) =>
            {
                if (r.Balance is <= 0 or > 1_000_000_000m || r.CurrentRate is < 0 or > 100 || r.NewRate is < 0 or > 100
                    || r.RemainingMonths is < 1 or > 600 || r.NewTermMonths is < 1 or > 600 || r.ClosingCosts is < 0 or > 10_000_000m)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["refinance"] = ["Provide a positive balance, rates 0-100%, terms of 1-600 months and non-negative closing costs."] });
                }

                return Results.Ok(Simulators.Refinance(r.Balance, r.CurrentRate, r.RemainingMonths, r.NewRate, r.NewTermMonths, r.ClosingCosts));
            })
            .WithName("SimulateRefinance")
            .WithSummary("Refinance break-even and lifetime interest comparison");

        group.MapPost("/simulate/ladder", async (StrategyService svc, [FromBody] LadderRequest r, CancellationToken ct) =>
            {
                if (r.Amount is < 400 or > 1_000_000_000m)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["Provide an amount of at least $400."] });
                }

                return Results.Ok(LadderPlanner.Build(r.Amount, await svc.GetRatesAsync(ct)));
            })
            .WithName("SimulateLadder")
            .WithSummary("CD ladder vs high-yield savings at current rates");

        return app;
    }
}
