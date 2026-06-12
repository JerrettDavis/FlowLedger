using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Forecasting;

/// <summary>
/// Deterministic, pure forecast engine (PLAN.md §11).
///
/// Algorithm (O(E log E) where E = number of events across all accounts):
///
/// 1. Validate inputs (missing balances, empty horizon, currency guard).
/// 2. For each account:
///    a. Determine which actual transactions are already realized (Posted/Pending/Matched/Reconciled).
///       These are applied verbatim at their EffectiveDate.
///    b. Determine the set of matched planned occurrence IDs (from actuals) — these must NOT
///       be re-applied from the plan side to avoid double-counting.
///    c. Merge planned occurrences: only those NOT already matched (and not superseded by an actual).
///    d. Expand recurring flow schedules into dates; suppress any date that is already covered
///       by an existing planned occurrence (same flow + date) to avoid duplication between
///       Money Plan rows and freshly-expanded ones.
///    e. Sort all events by date → apply sequentially to produce running balance series.
///    f. Record contributing items for explainability.
/// 3. Build aggregate series by summing per-account balances on each event date.
/// 4. Compute low-water marks and overdraft warnings.
/// 5. Evaluate goal affordability from the account surplus series.
/// </summary>
public sealed class ForecastEngine : IForecastEngine
{
    public ForecastResult Run(ForecastRequest request)
    {
        // ── Validate ─────────────────────────────────────────────────────────

        ArgumentNullException.ThrowIfNull(request);

        if (!request.Horizon.End.HasValue)
        {
            throw new ForecastInputException("Forecast horizon must have a finite end date.");
        }

        var horizonEnd = request.Horizon.End.Value;

        if (horizonEnd < request.Horizon.Start)
        {
            throw new ForecastInputException("Forecast horizon end must be on or after start.");
        }

        // Determine which accounts to forecast
        var accountIds = request.AccountIds is { Count: > 0 }
            ? request.AccountIds
            : request.StartingBalances.Keys.ToList();

        if (accountIds.Count == 0)
        {
            throw new ForecastInputException("At least one account must be included in the forecast.");
        }

        foreach (var aid in accountIds)
        {
            if (!request.StartingBalances.ContainsKey(aid))
            {
                throw new ForecastInputException($"Starting balance not provided for account {aid}.");
            }
        }

        // ── Build per-account series ──────────────────────────────────────────

        var accountSeries = new List<AccountForecastSeries>(accountIds.Count);

        // Index actuals by account
        var actualsByAccount = request.ActualTransactions
            .GroupBy(t => t.AccountId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Set of matched occurrence IDs (from actuals): these suppress planned occurrences
        var matchedOccurrenceIds = new HashSet<PlannedOccurrenceId>(
            request.ActualTransactions
                .Where(t => t.MatchedOccurrenceId.HasValue)
                .Select(t => t.MatchedOccurrenceId!.Value));

        // Set of matched occurrence IDs from planned occurrences themselves
        foreach (var occ in request.PlannedOccurrences.Where(o => o.MatchedTransactionId.HasValue))
        {
            matchedOccurrenceIds.Add(occ.OccurrenceId);
        }

        // Index planned occurrences by account; group by (flowId, date) to deduplicate
        var plannedByAccount = request.PlannedOccurrences
            .GroupBy(o => o.AccountId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Index recurring flows by account
        var flowsByAccount = request.RecurringFlows
            .GroupBy(f => f.AccountId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var accountId in accountIds)
        {
            var startingBalance = request.StartingBalances[accountId];
            var series = BuildAccountSeries(
                accountId,
                startingBalance,
                request.Horizon,
                horizonEnd,
                request.AsOf,
                actualsByAccount.GetValueOrDefault(accountId) ?? [],
                plannedByAccount.GetValueOrDefault(accountId) ?? [],
                flowsByAccount.GetValueOrDefault(accountId) ?? [],
                matchedOccurrenceIds);

            accountSeries.Add(series);
        }

        // ── Aggregate series ──────────────────────────────────────────────────

        var aggregateSeries = BuildAggregateSeries(accountSeries);

        // ── Low-water marks and overdraft warnings ────────────────────────────

        var lowWaterMarks = new List<AccountLowWaterMark>(accountSeries.Count);
        var overdraftWarnings = new List<OverdraftWarning>();

        foreach (var series in accountSeries)
        {
            if (series.Points.Count == 0)
            {
                continue;
            }

            var minPoint = series.Points.MinBy(p => p.Balance.Amount)!;
            lowWaterMarks.Add(new AccountLowWaterMark
            {
                AccountId = series.AccountId,
                MinBalance = minPoint.Balance,
                Date = minPoint.Date
            });

            // Overdraft: first point where balance < 0
            var firstBreach = series.Points.FirstOrDefault(p => p.Balance.IsNegative);
            if (firstBreach is not null)
            {
                overdraftWarnings.Add(new OverdraftWarning
                {
                    AccountId = series.AccountId,
                    FirstBreachDate = firstBreach.Date,
                    ProjectedBalance = firstBreach.Balance
                });
            }
        }

        AggregateLowWaterMark aggregateLowWater;
        if (aggregateSeries.Count > 0)
        {
            var aggMin = aggregateSeries.MinBy(p => p.Balance.Amount)!;
            aggregateLowWater = new AggregateLowWaterMark
            {
                MinBalance = aggMin.Balance,
                Date = aggMin.Date
            };
        }
        else
        {
            // Fallback: use first account's starting balance currency
            var currency = request.StartingBalances.Values.First().Currency;
            aggregateLowWater = new AggregateLowWaterMark
            {
                MinBalance = Money.Zero(currency),
                Date = request.Horizon.Start
            };
        }

        // ── Goal affordability ────────────────────────────────────────────────

        var goalOutcomes = new List<GoalAffordabilityResult>(request.Goals.Count);
        foreach (var goal in request.Goals)
        {
            var outcome = EvaluateGoalAffordability(goal, accountSeries, request.AsOf);
            goalOutcomes.Add(outcome);
        }

        return new ForecastResult
        {
            ForecastRunId = Guid.NewGuid(),
            AsOf = request.AsOf,
            Horizon = request.Horizon,
            AccountSeries = accountSeries,
            AggregateSeries = aggregateSeries,
            LowWaterMarks = lowWaterMarks,
            AggregateLowWaterMark = aggregateLowWater,
            OverdraftWarnings = overdraftWarnings,
            GoalOutcomes = goalOutcomes
        };
    }

    // ── Per-account series builder ────────────────────────────────────────────

    private static AccountForecastSeries BuildAccountSeries(
        AccountId accountId,
        Money startingBalance,
        DateOnlyRange horizon,
        DateOnly horizonEnd,
        DateOnly asOf,
        List<ForecastTransactionInput> actuals,
        List<ForecastOccurrenceInput> plannedOccurrences,
        List<ForecastFlowInput> recurringFlows,
        HashSet<PlannedOccurrenceId> matchedOccurrenceIds)
    {
        // Collect all events for this account as (date, ForecastLineItem)
        var events = new List<(DateOnly Date, ForecastLineItem Item)>();

        // ── 1. Actual transactions (already realized) ─────────────────────────
        // Only include transactions with statuses that are "real" (not planned).
        // Actuals within the horizon are included regardless of asOf; they represent
        // already-settled reality the starting balance may not yet reflect.
        foreach (var tx in actuals)
        {
            if (tx.Status is TransactionStatus.Planned or TransactionStatus.Skipped
                or TransactionStatus.Ignored or TransactionStatus.NeedsReview)
            {
                continue;
            }

            if (tx.EffectiveDate < horizon.Start || tx.EffectiveDate > horizonEnd)
            {
                continue;
            }

            var delta = SignedDelta(tx.Amount, tx.Direction);
            events.Add((tx.EffectiveDate, new ForecastLineItem
            {
                Source = ForecastItemSource.ActualTransaction,
                SourceId = tx.TransactionId.Value,
                Label = tx.Description ?? "Transaction",
                Amount = tx.Amount,
                Direction = tx.Direction,
                BalanceDelta = delta,
                IsActual = true
            }));
        }

        // Set of (flowId, date) covered by a persisted planned occurrence so
        // freshly-expanded occurrences for the same (flow, date) are suppressed.
        var coveredByPlan = new HashSet<(Guid FlowId, DateOnly Date)>();

        // ── 2. Planned occurrences (from Money Plan) — not already matched ─────
        foreach (var occ in plannedOccurrences)
        {
            if (matchedOccurrenceIds.Contains(occ.OccurrenceId))
            {
                // Already matched to an actual — actual side already added above;
                // record the plan (flow+date) as covered to suppress expansion.
                coveredByPlan.Add((occ.FlowId.Value, occ.PlannedDate));
                continue;
            }

            if (occ.PlannedDate < horizon.Start || occ.PlannedDate > horizonEnd)
            {
                coveredByPlan.Add((occ.FlowId.Value, occ.PlannedDate));
                continue;
            }

            // Suppress occurrences that are in the past (before asOf) — those should
            // already be reflected in the starting balance.
            if (occ.PlannedDate < asOf)
            {
                coveredByPlan.Add((occ.FlowId.Value, occ.PlannedDate));
                continue;
            }

            coveredByPlan.Add((occ.FlowId.Value, occ.PlannedDate));

            var delta = SignedDelta(occ.PlannedAmount, occ.Direction);
            events.Add((occ.PlannedDate, new ForecastLineItem
            {
                Source = ForecastItemSource.PlannedOccurrence,
                SourceId = occ.OccurrenceId.Value,
                Label = $"Planned ({occ.FlowId})",
                Amount = occ.PlannedAmount,
                Direction = occ.Direction,
                BalanceDelta = delta,
                IsActual = false
            }));
        }

        // ── 3. Expand recurring flows; skip dates already covered by plan ──────
        var expandHorizon = new DateOnlyRange(
            asOf > horizon.Start ? asOf : horizon.Start,
            horizonEnd);

        foreach (var flow in recurringFlows)
        {
            // Currency guard: all flows on an account must share the account currency.
            // We check against startingBalance.Currency.
            if (flow.Amount.Currency != startingBalance.Currency)
            {
                throw new ForecastInputException(
                    $"Flow '{flow.FlowId}' currency '{flow.Amount.Currency.Code}' does not match " +
                    $"account '{accountId}' currency '{startingBalance.Currency.Code}'. " +
                    "Cannot mix currencies in a single account forecast.");
            }

            var dates = RecurrenceExpander.Expand(
                flow.Pattern,
                flow.FlowStart,
                flow.FlowEnd,
                expandHorizon);

            foreach (var date in dates)
            {
                if (coveredByPlan.Contains((flow.FlowId.Value, date)))
                {
                    continue; // Suppressed — either already matched or a Money Plan row exists
                }

                var delta = SignedDelta(flow.Amount, flow.Direction);
                events.Add((date, new ForecastLineItem
                {
                    Source = ForecastItemSource.ExpandedOccurrence,
                    SourceId = flow.FlowId.Value,
                    Label = flow.Name ?? flow.FlowId.ToString(),
                    Amount = flow.Amount,
                    Direction = flow.Direction,
                    BalanceDelta = delta,
                    IsActual = false
                }));
            }
        }

        // ── 4. Sort events by date, then group by date ────────────────────────

        var byDate = events
            .GroupBy(e => e.Date)
            .OrderBy(g => g.Key)
            .ToList();

        // ── 5. Walk dates; accumulate running balance ─────────────────────────

        var points = new List<ForecastPoint>(byDate.Count + 1);
        var runningBalance = startingBalance;

        foreach (var group in byDate)
        {
            var items = group.Select(e => e.Item).ToList();
            var net = items.Aggregate(
                Money.Zero(startingBalance.Currency),
                (acc, item) => acc + item.BalanceDelta);

            runningBalance = runningBalance + net;

            points.Add(new ForecastPoint
            {
                Date = group.Key,
                Balance = runningBalance,
                NetChange = net,
                ContributingItems = items.AsReadOnly()
            });
        }

        return new AccountForecastSeries
        {
            AccountId = accountId,
            StartingBalance = startingBalance,
            Points = points.AsReadOnly()
        };
    }

    // ── Aggregate series ──────────────────────────────────────────────────────

    private static IReadOnlyList<AggregateForecastPoint> BuildAggregateSeries(
        List<AccountForecastSeries> accountSeries)
    {
        if (accountSeries.Count == 0)
        {
            return Array.Empty<AggregateForecastPoint>();
        }

        // Collect all distinct dates across all series
        var allDates = accountSeries
            .SelectMany(s => s.Points.Select(p => p.Date))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (allDates.Count == 0)
        {
            return Array.Empty<AggregateForecastPoint>();
        }

        // Verify single currency across accounts (guard)
        var currencies = accountSeries
            .Select(s => s.StartingBalance.Currency)
            .Distinct()
            .ToList();

        // We only sum same-currency accounts; mixed-currency aggregation is skipped
        // with a single-currency assumption (callers should separate by currency).
        if (currencies.Count > 1)
        {
            // Return empty aggregate rather than silently mixing currencies
            return Array.Empty<AggregateForecastPoint>();
        }

        var currency = currencies[0];

        // For each date, get each account's balance at or before that date
        var result = new List<AggregateForecastPoint>(allDates.Count);
        foreach (var date in allDates)
        {
            var sum = Money.Zero(currency);
            foreach (var series in accountSeries)
            {
                // Find the last point on or before this date
                var latestPoint = series.Points
                    .Where(p => p.Date <= date)
                    .LastOrDefault();

                sum = sum + (latestPoint?.Balance ?? series.StartingBalance);
            }
            result.Add(new AggregateForecastPoint { Date = date, Balance = sum });
        }

        return result;
    }

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
            var monthsLeft = MonthsBetween(asOf, goal.TargetDate.Value);
            requiredMonthly = monthsLeft > 0
                ? Math.Round(remaining.Amount / monthsLeft, 2)
                : null;
        }

        // Find earliest date where the projected account balance exceeds remaining amount
        // (i.e. projected surplus covers the goal)
        DateOnly? affordableBy = null;
        if (linkedSeries is not null)
        {
            // Walk the series; find first point where balance >= remaining
            var runningBalance = linkedSeries.StartingBalance;
            foreach (var point in linkedSeries.Points)
            {
                if (point.Balance >= remaining)
                {
                    affordableBy = point.Date;
                    break;
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

    private static decimal MonthsBetween(DateOnly from, DateOnly to)
    {
        var months = (to.Year - from.Year) * 12 + (to.Month - from.Month);
        // Add fractional month from day difference
        var dayFraction = (to.Day - from.Day) / 30m;
        return Math.Max(0m, months + dayFraction);
    }
}
