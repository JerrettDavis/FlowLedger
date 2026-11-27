using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Forecasting;

public sealed partial class ForecastEngine
{
    // ── Goal affordability calculator ─────────────────────────────────────────

    private static GoalAffordabilityResult EvaluateGoalAffordability(
        GoalAffordabilityInput goal,
        List<AccountForecastSeries> accountSeries,
        DateOnly asOf)
    {
        var remaining = goal.TargetAmount - goal.CurrentBalance;

        // If already funded
        if (remaining.Amount <= 0m)
        {
            return new GoalAffordabilityResult
            {
                GoalId = goal.GoalId,
                Name = goal.Name,
                TargetAmount = goal.TargetAmount,
                CurrentBalance = goal.CurrentBalance,
                RemainingAmount = Money.Zero(goal.TargetAmount.Currency),
                IsAffordable = true,
                AffordableByDate = asOf,
                RequiredMonthlyContribution = 0m
            };
        }

        // Find the linked account series
        var linkedSeries = accountSeries.FirstOrDefault(s => s.AccountId == goal.LinkedAccountId);

        // Required monthly contribution given a target date
        decimal? requiredMonthly = null;
        if (goal.TargetDate.HasValue && goal.TargetDate.Value > asOf)
        {
            requiredMonthly = Goals.GoalMath.MonthlyNeeded(remaining.Amount, goal.TargetDate, asOf);
        }

        // Find earliest date where the projected account balance exceeds remaining amount
        // (i.e. projected surplus covers the goal)
        DateOnly? affordableBy = null;
        if (linkedSeries is not null)
        {
            if (linkedSeries.StartingBalance >= remaining)
            {
                // The linked account already covers the goal as of asOf, with or without any scheduled events.
                affordableBy = asOf;
            }
            else
            {
                // Walk the series; find first point where balance >= remaining
                foreach (var point in linkedSeries.Points)
                {
                    if (point.Balance >= remaining)
                    {
                        affordableBy = point.Date;
                        break;
                    }
                }
            }
        }

        return new GoalAffordabilityResult
        {
            GoalId = goal.GoalId,
            Name = goal.Name,
            TargetAmount = goal.TargetAmount,
            CurrentBalance = goal.CurrentBalance,
            RemainingAmount = remaining,
            IsAffordable = affordableBy.HasValue,
            AffordableByDate = affordableBy,
            RequiredMonthlyContribution = requiredMonthly
        };
    }

    // ── Pure helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the signed balance delta: Credits increase the balance (+); Debits reduce it (−).
    /// </summary>
    private static Money SignedDelta(Money amount, TransactionDirection direction) =>
        direction == TransactionDirection.Credit ? amount : amount.Negate();
}
