using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;
using System.Diagnostics;

namespace FlowLedger.Application.Tests.Features.Forecasting;

/// <summary>
/// Deterministic golden tests, reconciliation, overdraft detection, and goal affordability
/// for <see cref="ForecastEngine"/>.
/// </summary>
public sealed class ForecastEngineTests
{
    private static readonly AccountId Checking = AccountId.New();
    private static readonly AccountId Savings = AccountId.New();
    private static Money M(decimal amount) => new(amount, new Currency("USD"));

    private static ForecastEngine Engine => new();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ForecastFlowInput Flow(
        AccountId accountId,
        decimal amount,
        TransactionDirection direction,
        RecurrencePattern pattern,
        DateOnly flowStart,
        DateOnly? flowEnd = null,
        string? name = null)
    {
        return new ForecastFlowInput
        {
            FlowId = RecurringFlowId.New(),
            AccountId = accountId,
            Amount = new Money(amount, new Currency("USD")),
            Direction = direction,
            Pattern = pattern,
            FlowStart = flowStart,
            FlowEnd = flowEnd,
            Name = name ?? "Flow"
        };
    }

    private static ForecastRequest SimpleRequest(
        DateOnly asOf,
        DateOnly horizonEnd,
        IReadOnlyList<ForecastFlowInput> flows,
        decimal startBalance = 1000m,
        AccountId? accountId = null)
    {
        var aid = accountId ?? Checking;
        return new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, horizonEnd),
            StartingBalances = new Dictionary<AccountId, Money> { [aid] = new Money(startBalance, new Currency("USD")) },
            AccountIds = [aid],
            RecurringFlows = flows,
        };
    }

    // ── Golden test: monthly rent + biweekly payroll ───────────────────────

    [Fact]
    public void Golden_monthly_rent_and_biweekly_payroll_exact_series()
    {
        // Setup:
        // Account starts at $1,000
        // Payroll: $2,000 credit every 2 weeks, first on Jan 3 (Friday)
        // Rent: $1,500 debit on the 1st of each month
        // Horizon: Jan 1 – Feb 28, 2025
        // AsOf: Jan 1, 2025

        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 2, 28);

        var payroll = Flow(Checking, 2000m, TransactionDirection.Credit,
            RecurrencePattern.EveryNWeeks(2, DayOfWeek.Friday),
            flowStart: asOf, name: "Payroll");

        var rent = Flow(Checking, 1500m, TransactionDirection.Debit,
            RecurrencePattern.Monthly(1),
            flowStart: asOf, name: "Rent");

        var request = SimpleRequest(asOf, end, [payroll, rent], startBalance: 1000m);
        var result = Engine.Run(request);

        // Expected events (chronological):
        // Jan 1:  Rent -1500 → balance = 1000 - 1500 = -500
        // Jan 3:  Payroll +2000 → -500 + 2000 = 1500
        // Jan 17: Payroll +2000 → 1500 + 2000 = 3500
        // Jan 31: Payroll +2000 → 3500 + 2000 = 5500
        // Feb 1:  Rent -1500 → 5500 - 1500 = 4000
        // Feb 14: Payroll +2000 → 4000 + 2000 = 6000
        // Feb 28: Payroll +2000 → 6000 + 2000 = 8000

        var series = result.AccountSeries.Should().ContainSingle().Subject;
        series.AccountId.Should().Be(Checking);

        var points = series.Points;
        points.Should().HaveCount(7);

        points[0].Date.Should().Be(new DateOnly(2025, 1, 1));
        points[0].Balance.Amount.Should().Be(-500m);

        points[1].Date.Should().Be(new DateOnly(2025, 1, 3));
        points[1].Balance.Amount.Should().Be(1500m);

        points[2].Date.Should().Be(new DateOnly(2025, 1, 17));
        points[2].Balance.Amount.Should().Be(3500m);

        points[3].Date.Should().Be(new DateOnly(2025, 1, 31));
        points[3].Balance.Amount.Should().Be(5500m);

        points[4].Date.Should().Be(new DateOnly(2025, 2, 1));
        points[4].Balance.Amount.Should().Be(4000m);

        points[5].Date.Should().Be(new DateOnly(2025, 2, 14));
        points[5].Balance.Amount.Should().Be(6000m);

        points[6].Date.Should().Be(new DateOnly(2025, 2, 28));
        points[6].Balance.Amount.Should().Be(8000m);
    }

    // ── Low-water mark detection ──────────────────────────────────────────────

    [Fact]
    public void LowWaterMark_identifies_minimum_projected_balance_and_date()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 2, 28);

        // Large rent hits Jan 1 → dips deeply negative, then payrolls recover
        var payroll = Flow(Checking, 2000m, TransactionDirection.Credit,
            RecurrencePattern.EveryNWeeks(2, DayOfWeek.Friday), flowStart: asOf);
        var rent = Flow(Checking, 1500m, TransactionDirection.Debit,
            RecurrencePattern.Monthly(1), flowStart: asOf);

        var request = SimpleRequest(asOf, end, [payroll, rent], startBalance: 1000m);
        var result = Engine.Run(request);

        var lwm = result.LowWaterMarks.Should().ContainSingle().Subject;
        lwm.AccountId.Should().Be(Checking);
        lwm.Date.Should().Be(new DateOnly(2025, 1, 1));
        lwm.MinBalance.Amount.Should().Be(-500m);
    }

    // ── Overdraft detection ───────────────────────────────────────────────────

    [Fact]
    public void OverdraftWarning_emitted_with_correct_first_breach_date()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 31);

        // Start $200. Single large debit on Jan 5 of $500 → breach Jan 5
        var largeBill = Flow(Checking, 500m, TransactionDirection.Debit,
            RecurrencePattern.Monthly(5), flowStart: asOf, name: "BigBill");

        var request = SimpleRequest(asOf, end, [largeBill], startBalance: 200m);
        var result = Engine.Run(request);

        result.OverdraftWarnings.Should().ContainSingle(w =>
            w.AccountId == Checking &&
            w.FirstBreachDate == new DateOnly(2025, 1, 5) &&
            w.ProjectedBalance.Amount == -300m);
    }

    [Fact]
    public void No_overdraft_warning_when_balance_stays_positive()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 31);

        var subscription = Flow(Checking, 15m, TransactionDirection.Debit,
            RecurrencePattern.Monthly(10), flowStart: asOf);

        var request = SimpleRequest(asOf, end, [subscription], startBalance: 1000m);
        var result = Engine.Run(request);

        result.OverdraftWarnings.Should().BeEmpty();
    }

    // ── Planned-vs-actual reconciliation: no double-counting ─────────────────

    [Fact]
    public void Matched_actual_replaces_planned_occurrence_no_double_count()
    {
        // A recurring flow has one planned occurrence on Jan 10 for $200.
        // An actual Posted transaction for $195 was matched to that occurrence.
        // The forecast should apply the actual ($195), NOT the planned ($200),
        // and must NOT apply both (which would be -$395).

        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 31);
        var flowId = RecurringFlowId.New();
        var occurrenceId = PlannedOccurrenceId.New();
        var actualTxId = TransactionId.New();

        // Planned occurrence on Jan 10, matched to an actual
        var planned = new ForecastOccurrenceInput
        {
            OccurrenceId = occurrenceId,
            FlowId = flowId,
            AccountId = Checking,
            PlannedAmount = new Money(200m, new Currency("USD")),
            Direction = TransactionDirection.Debit,
            PlannedDate = new DateOnly(2025, 1, 10),
            MatchedTransactionId = actualTxId    // ← matched
        };

        // Actual transaction for $195 (real amount), linked back to occurrence
        var actual = new ForecastTransactionInput
        {
            TransactionId = actualTxId,
            AccountId = Checking,
            Amount = new Money(195m, new Currency("USD")),
            Direction = TransactionDirection.Debit,
            EffectiveDate = new DateOnly(2025, 1, 10),
            Status = TransactionStatus.Matched,
            MatchedOccurrenceId = occurrenceId,
            Description = "Utility bill"
        };

        // Recurring flow that would re-expand Jan 10 if not suppressed
        var flow = new ForecastFlowInput
        {
            FlowId = flowId,
            AccountId = Checking,
            Amount = new Money(200m, new Currency("USD")),
            Direction = TransactionDirection.Debit,
            Pattern = RecurrencePattern.Monthly(10),
            FlowStart = new DateOnly(2025, 1, 1),
            Name = "Utility"
        };

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, end),
            StartingBalances = new Dictionary<AccountId, Money>
                { [Checking] = new Money(1000m, new Currency("USD")) },
            AccountIds = [Checking],
            RecurringFlows = [flow],
            PlannedOccurrences = [planned],
            ActualTransactions = [actual]
        };

        var result = Engine.Run(request);

        var series = result.AccountSeries.Should().ContainSingle().Subject;
        var jan10 = series.Points.Should().ContainSingle(p => p.Date == new DateOnly(2025, 1, 10)).Subject;

        // Should see exactly ONE item on Jan 10 (the actual, $195)
        jan10.ContributingItems.Should().ContainSingle();
        jan10.ContributingItems[0].Amount.Amount.Should().Be(195m);
        jan10.ContributingItems[0].IsActual.Should().BeTrue();
        jan10.ContributingItems[0].Source.Should().Be(ForecastItemSource.ActualTransaction);

        // Balance should be 1000 - 195 = 805, NOT 1000 - 395 (double-count)
        jan10.Balance.Amount.Should().Be(805m);
    }

    [Fact]
    public void Unmatched_planned_occurrence_is_included_in_forecast()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 31);
        var flowId = RecurringFlowId.New();
        var occurrenceId = PlannedOccurrenceId.New();

        // Planned occurrence on Jan 15, NOT yet matched
        var planned = new ForecastOccurrenceInput
        {
            OccurrenceId = occurrenceId,
            FlowId = flowId,
            AccountId = Checking,
            PlannedAmount = new Money(300m, new Currency("USD")),
            Direction = TransactionDirection.Debit,
            PlannedDate = new DateOnly(2025, 1, 15),
            MatchedTransactionId = null   // ← not matched
        };

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, end),
            StartingBalances = new Dictionary<AccountId, Money>
                { [Checking] = new Money(1000m, new Currency("USD")) },
            AccountIds = [Checking],
            RecurringFlows = [],
            PlannedOccurrences = [planned],
            ActualTransactions = []
        };

        var result = Engine.Run(request);
        var series = result.AccountSeries.Should().ContainSingle().Subject;
        var jan15 = series.Points.Should().ContainSingle(p => p.Date == new DateOnly(2025, 1, 15)).Subject;

        jan15.ContributingItems.Should().ContainSingle(i =>
            i.Source == ForecastItemSource.PlannedOccurrence &&
            i.Amount.Amount == 300m &&
            !i.IsActual);

        jan15.Balance.Amount.Should().Be(700m);
    }

    // ── Explainability: contributing items ───────────────────────────────────

    [Fact]
    public void ForecastPoint_lists_all_contributing_items_on_multi_event_day()
    {
        // Two flows hit the same day: payroll credit + rent debit on Jan 1
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 1);

        var payroll = Flow(Checking, 2000m, TransactionDirection.Credit,
            RecurrencePattern.Monthly(1), flowStart: asOf, name: "Payroll");
        var rent = Flow(Checking, 1500m, TransactionDirection.Debit,
            RecurrencePattern.Monthly(1), flowStart: asOf, name: "Rent");

        var request = SimpleRequest(asOf, end, [payroll, rent], startBalance: 1000m);
        var result = Engine.Run(request);

        var series = result.AccountSeries.Should().ContainSingle().Subject;
        var jan1 = series.Points.Should().ContainSingle().Subject;

        jan1.ContributingItems.Should().HaveCount(2);
        jan1.ContributingItems.Should().Contain(i => i.Label == "Payroll" && i.Direction == TransactionDirection.Credit);
        jan1.ContributingItems.Should().Contain(i => i.Label == "Rent" && i.Direction == TransactionDirection.Debit);

        // Net: +2000 - 1500 = +500, balance = 1500
        jan1.NetChange.Amount.Should().Be(500m);
        jan1.Balance.Amount.Should().Be(1500m);
    }

    // ── Goal affordability ────────────────────────────────────────────────────

    [Fact]
    public void Goal_affordable_when_projected_balance_exceeds_remaining()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 6, 30);
        var goalId = Guid.NewGuid();

        // Payroll every 2 weeks, no expenses → balance grows steadily
        var payroll = Flow(Checking, 2000m, TransactionDirection.Credit,
            RecurrencePattern.EveryNWeeks(2, DayOfWeek.Friday), flowStart: asOf);

        var goal = new GoalAffordabilityInput
        {
            GoalId = goalId,
            Name = "Emergency Fund",
            TargetAmount = new Money(5000m, new Currency("USD")),
            CurrentBalance = new Money(0m, new Currency("USD")),
            TargetDate = new DateOnly(2025, 6, 30),
            LinkedAccountId = Checking
        };

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, end),
            StartingBalances = new Dictionary<AccountId, Money>
                { [Checking] = new Money(0m, new Currency("USD")) },
            AccountIds = [Checking],
            RecurringFlows = [payroll],
            Goals = [goal]
        };

        var result = Engine.Run(request);
        var goalResult = result.GoalOutcomes.Should().ContainSingle(g => g.GoalId == goalId).Subject;

        goalResult.IsAffordable.Should().BeTrue();
        goalResult.AffordableByDate.Should().NotBeNull();
        goalResult.AffordableByDate!.Value.Should().BeBefore(new DateOnly(2025, 6, 30).AddDays(1));
        goalResult.RemainingAmount.Amount.Should().Be(5000m);
    }

    [Fact]
    public void Goal_not_affordable_when_projected_surplus_never_covers_amount()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 3, 31);
        var goalId = Guid.NewGuid();

        // Tiny payroll, large goal
        var payroll = Flow(Checking, 100m, TransactionDirection.Credit,
            RecurrencePattern.Monthly(15), flowStart: asOf);

        var goal = new GoalAffordabilityInput
        {
            GoalId = goalId,
            Name = "Vacation",
            TargetAmount = new Money(50000m, new Currency("USD")),
            CurrentBalance = new Money(0m, new Currency("USD")),
            TargetDate = new DateOnly(2025, 3, 31),
            LinkedAccountId = Checking
        };

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, end),
            StartingBalances = new Dictionary<AccountId, Money>
                { [Checking] = new Money(0m, new Currency("USD")) },
            AccountIds = [Checking],
            RecurringFlows = [payroll],
            Goals = [goal]
        };

        var result = Engine.Run(request);
        var goalResult = result.GoalOutcomes.Should().ContainSingle(g => g.GoalId == goalId).Subject;

        goalResult.IsAffordable.Should().BeFalse();
        goalResult.AffordableByDate.Should().BeNull();
        goalResult.RequiredMonthlyContribution.Should().NotBeNull();
        goalResult.RequiredMonthlyContribution!.Value.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Goal_already_funded_returns_affordable_immediately()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 31);
        var goalId = Guid.NewGuid();

        var goal = new GoalAffordabilityInput
        {
            GoalId = goalId,
            Name = "Car Fund",
            TargetAmount = new Money(1000m, new Currency("USD")),
            CurrentBalance = new Money(1200m, new Currency("USD")),  // already over target
            TargetDate = null,
            LinkedAccountId = Checking
        };

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, end),
            StartingBalances = new Dictionary<AccountId, Money>
                { [Checking] = new Money(5000m, new Currency("USD")) },
            AccountIds = [Checking],
            RecurringFlows = [],
            Goals = [goal]
        };

        var result = Engine.Run(request);
        var goalResult = result.GoalOutcomes.Should().ContainSingle().Subject;

        goalResult.IsAffordable.Should().BeTrue();
        goalResult.AffordableByDate.Should().Be(asOf);
        goalResult.RemainingAmount.Amount.Should().Be(0m);
    }

    // ── Multi-account aggregate series ────────────────────────────────────────

    [Fact]
    public void Aggregate_series_sums_balances_across_accounts()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 15);

        // Checking: starts $1000, payroll Jan 3 (+$500)
        var checkingPayroll = new ForecastFlowInput
        {
            FlowId = RecurringFlowId.New(),
            AccountId = Checking,
            Amount = new Money(500m, new Currency("USD")),
            Direction = TransactionDirection.Credit,
            Pattern = RecurrencePattern.Weekly(DayOfWeek.Friday),
            FlowStart = asOf,
            Name = "CheckingPayroll"
        };

        // Savings: starts $500, deposit Jan 10 (+$200)
        var savingsDeposit = new ForecastFlowInput
        {
            FlowId = RecurringFlowId.New(),
            AccountId = Savings,
            Amount = new Money(200m, new Currency("USD")),
            Direction = TransactionDirection.Credit,
            Pattern = RecurrencePattern.Monthly(10),
            FlowStart = asOf,
            Name = "SavingsDeposit"
        };

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, end),
            StartingBalances = new Dictionary<AccountId, Money>
            {
                [Checking] = new Money(1000m, new Currency("USD")),
                [Savings]  = new Money(500m, new Currency("USD"))
            },
            AccountIds = [Checking, Savings],
            RecurringFlows = [checkingPayroll, savingsDeposit],
        };

        var result = Engine.Run(request);

        // Jan 3: Checking +500 → Checking=1500, Savings=500 → aggregate=2000
        var jan3Agg = result.AggregateSeries.FirstOrDefault(p => p.Date == new DateOnly(2025, 1, 3));
        jan3Agg.Should().NotBeNull();
        jan3Agg!.Balance.Amount.Should().Be(2000m);

        // Jan 10 is also a Friday, so checking gets ANOTHER +500 → Checking=2000, Savings gets +200 → 700
        // aggregate Jan 10 = 2000 + 700 = 2700
        var jan10Agg = result.AggregateSeries.FirstOrDefault(p => p.Date == new DateOnly(2025, 1, 10));
        jan10Agg.Should().NotBeNull();
        jan10Agg!.Balance.Amount.Should().Be(2700m);
    }

    // ── Input validation ──────────────────────────────────────────────────────

    [Fact]
    public void Throws_ForecastInputException_when_starting_balance_missing()
    {
        var missingId = AccountId.New();
        var request = new ForecastRequest
        {
            AsOf = new DateOnly(2025, 1, 1),
            Horizon = new DateOnlyRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 3, 31)),
            StartingBalances = new Dictionary<AccountId, Money>(),
            AccountIds = [missingId],
            RecurringFlows = []
        };

        var act = () => Engine.Run(request);
        act.Should().Throw<ForecastInputException>().WithMessage("*Starting balance*");
    }

    [Fact]
    public void Throws_ForecastInputException_for_open_ended_horizon()
    {
        var request = new ForecastRequest
        {
            AsOf = new DateOnly(2025, 1, 1),
            Horizon = new DateOnlyRange(new DateOnly(2025, 1, 1)),  // no end
            StartingBalances = new Dictionary<AccountId, Money>
                { [Checking] = new Money(1000m, new Currency("USD")) },
            AccountIds = [Checking],
            RecurringFlows = []
        };

        var act = () => Engine.Run(request);
        act.Should().Throw<ForecastInputException>().WithMessage("*finite*");
    }

    [Fact]
    public void Throws_ForecastInputException_for_currency_mismatch_in_flow()
    {
        var asOf = new DateOnly(2025, 1, 1);
        var end  = new DateOnly(2025, 1, 31);

        // Account balance in USD but flow in GBP
        var gbpFlow = new ForecastFlowInput
        {
            FlowId = RecurringFlowId.New(),
            AccountId = Checking,
            Amount = new Money(100m, new Currency("GBP")),  // ← wrong currency
            Direction = TransactionDirection.Debit,
            Pattern = RecurrencePattern.Monthly(5),
            FlowStart = asOf
        };

        var request = new ForecastRequest
        {
            AsOf = asOf,
            Horizon = new DateOnlyRange(asOf, end),
            StartingBalances = new Dictionary<AccountId, Money>
                { [Checking] = new Money(1000m, new Currency("USD")) },
            AccountIds = [Checking],
            RecurringFlows = [gbpFlow]
        };

        var act = () => Engine.Run(request);
        act.Should().Throw<ForecastInputException>().WithMessage("*currency*");
    }

    // ── Determinism: same inputs → same output ─────────────────────────────

    [Fact]
    public void Engine_is_deterministic_same_inputs_produce_identical_results()
    {
        var asOf = new DateOnly(2025, 3, 1);
        var end  = new DateOnly(2025, 5, 31);

        var payroll = Flow(Checking, 2500m, TransactionDirection.Credit,
            RecurrencePattern.EveryNWeeks(2, DayOfWeek.Friday), flowStart: asOf);
        var rent = Flow(Checking, 1800m, TransactionDirection.Debit,
            RecurrencePattern.Monthly(1), flowStart: asOf);
        var utilities = Flow(Checking, 120m, TransactionDirection.Debit,
            RecurrencePattern.Monthly(15), flowStart: asOf);

        var request = SimpleRequest(asOf, end, [payroll, rent, utilities], startBalance: 3000m);

        var result1 = Engine.Run(request);
        var result2 = Engine.Run(request);

        // Compare account series balance series values
        var balances1 = result1.AccountSeries[0].Points.Select(p => p.Balance.Amount).ToList();
        var balances2 = result2.AccountSeries[0].Points.Select(p => p.Balance.Amount).ToList();

        balances1.Should().BeEquivalentTo(balances2, "forecasts must be deterministic");
    }
}
