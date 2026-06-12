using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Forecasting;

// ─────────────────────────────────────────────────────────────────────────────
// Request
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Input to the forecasting engine. All fields are data-in; the engine performs
/// no I/O and no wall-clock reads — determinism is guaranteed by passing
/// <see cref="AsOf"/> explicitly.
/// </summary>
public sealed class ForecastRequest
{
    /// <summary>The date the forecast is "run from". Treated as today by the engine.</summary>
    public required DateOnly AsOf { get; init; }

    /// <summary>The horizon window to project (Start inclusive, End inclusive).</summary>
    public required DateOnlyRange Horizon { get; init; }

    /// <summary>
    /// Starting balance per account. Every AccountId in <see cref="AccountIds"/> must have
    /// an entry here; the engine throws <see cref="ForecastInputException"/> if one is missing.
    /// </summary>
    public required IReadOnlyDictionary<AccountId, Money> StartingBalances { get; init; }

    /// <summary>
    /// Subset of account IDs to include. If null or empty, all accounts represented in
    /// <see cref="StartingBalances"/> are included.
    /// </summary>
    public IReadOnlyList<AccountId>? AccountIds { get; init; }

    /// <summary>
    /// Recurring flows whose schedules are to be expanded. The engine generates occurrences
    /// from each flow's <see cref="Domain.Aggregates.RecurringFlow.Pattern"/> directly.
    /// </summary>
    public required IReadOnlyList<ForecastFlowInput> RecurringFlows { get; init; }

    /// <summary>
    /// Already-generated planned flow occurrences (e.g. from the Money Plan).
    /// These take precedence over freshly-expanded occurrences for the same
    /// (flow, date) pair to avoid double-counting.
    /// </summary>
    public IReadOnlyList<ForecastOccurrenceInput> PlannedOccurrences { get; init; } = [];

    /// <summary>
    /// Actual transactions (Posted, Pending, Matched, Reconciled) that have already
    /// hit the account. These are applied first; any planned occurrence whose matching
    /// transaction ID appears in this set is suppressed so it is not double-counted.
    /// </summary>
    public IReadOnlyList<ForecastTransactionInput> ActualTransactions { get; init; } = [];

    /// <summary>Optional savings goals to evaluate for affordability.</summary>
    public IReadOnlyList<GoalAffordabilityInput> Goals { get; init; } = [];
}

/// <summary>A recurring flow descriptor fed to the engine (no EF navigation objects).</summary>
public sealed class ForecastFlowInput
{
    public required RecurringFlowId FlowId { get; init; }
    public required AccountId AccountId { get; init; }
    public required Money Amount { get; init; }
    public required Domain.Aggregates.TransactionDirection Direction { get; init; }
    public required Domain.ValueObjects.RecurrencePattern Pattern { get; init; }
    public required DateOnly FlowStart { get; init; }
    public DateOnly? FlowEnd { get; init; }
    public string? Name { get; init; }
}

/// <summary>A pre-generated planned flow occurrence (from the Money Plan).</summary>
public sealed class ForecastOccurrenceInput
{
    public required PlannedOccurrenceId OccurrenceId { get; init; }
    public required RecurringFlowId FlowId { get; init; }
    public required AccountId AccountId { get; init; }
    public required Money PlannedAmount { get; init; }
    public required Domain.Aggregates.TransactionDirection Direction { get; init; }
    public required DateOnly PlannedDate { get; init; }
    /// <summary>If set, this occurrence has already been matched to an actual.</summary>
    public TransactionId? MatchedTransactionId { get; init; }
}

/// <summary>An actual transaction already on the account.</summary>
public sealed class ForecastTransactionInput
{
    public required TransactionId TransactionId { get; init; }
    public required AccountId AccountId { get; init; }
    public required Money Amount { get; init; }
    public required Domain.Aggregates.TransactionDirection Direction { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public required Domain.Aggregates.TransactionStatus Status { get; init; }
    /// <summary>If this actual matched a planned occurrence, its ID is here.</summary>
    public PlannedOccurrenceId? MatchedOccurrenceId { get; init; }
    public string? Description { get; init; }
}

/// <summary>A savings goal to evaluate against the forecast surplus.</summary>
public sealed class GoalAffordabilityInput
{
    public required Guid GoalId { get; init; }
    public required string Name { get; init; }
    public required Money TargetAmount { get; init; }
    public required Money CurrentBalance { get; init; }
    /// <summary>Desired completion date. May be null if open-ended.</summary>
    public DateOnly? TargetDate { get; init; }
    /// <summary>Account from which contributions are drawn (for surplus calculation).</summary>
    public required AccountId LinkedAccountId { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Result
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The complete output of a forecast run. All data is immutable.
/// </summary>
public sealed class ForecastResult
{
    public required Guid ForecastRunId { get; init; }
    public required DateOnly AsOf { get; init; }
    public required DateOnlyRange Horizon { get; init; }

    /// <summary>Per-account daily balance series.</summary>
    public required IReadOnlyList<AccountForecastSeries> AccountSeries { get; init; }

    /// <summary>Aggregate (household) running balance series across all forecasted accounts.</summary>
    public required IReadOnlyList<AggregateForecastPoint> AggregateSeries { get; init; }

    /// <summary>Low-water marks per account (minimum projected balance + date).</summary>
    public required IReadOnlyList<AccountLowWaterMark> LowWaterMarks { get; init; }

    /// <summary>Aggregate low-water mark across all accounts.</summary>
    public required AggregateLowWaterMark AggregateLowWaterMark { get; init; }

    /// <summary>Overdraft / negative-balance warnings, one entry per account that dips below zero.</summary>
    public required IReadOnlyList<OverdraftWarning> OverdraftWarnings { get; init; }

    /// <summary>Goal affordability outcomes.</summary>
    public required IReadOnlyList<GoalAffordabilityResult> GoalOutcomes { get; init; }
}

/// <summary>The complete balance projection for a single account.</summary>
public sealed class AccountForecastSeries
{
    public required AccountId AccountId { get; init; }
    public required Money StartingBalance { get; init; }
    /// <summary>
    /// Event-stepped points: one entry per date on which at least one item affects the balance,
    /// plus the starting point. The series is ordered by date ascending.
    /// </summary>
    public required IReadOnlyList<ForecastPoint> Points { get; init; }
}

/// <summary>
/// A single point in the forecast series. Fully explainable: every item that
/// contributed to this balance change is listed in <see cref="ContributingItems"/>.
/// </summary>
public sealed class ForecastPoint
{
    public required DateOnly Date { get; init; }
    /// <summary>Running balance after all items on this date are applied.</summary>
    public required Money Balance { get; init; }
    /// <summary>Net change from the previous point (sum of contributing item deltas).</summary>
    public required Money NetChange { get; init; }
    /// <summary>Every individual item that moved the balance on this date (EXPLAINABILITY).</summary>
    public required IReadOnlyList<ForecastLineItem> ContributingItems { get; init; }
}

/// <summary>
/// A single explainable line item that affects the balance on a given date.
/// </summary>
public sealed class ForecastLineItem
{
    public required ForecastItemSource Source { get; init; }
    /// <summary>
    /// ID of the source object:
    /// - ActualTransaction → TransactionId
    /// - PlannedOccurrence → PlannedOccurrenceId
    /// - ExpandedOccurrence → RecurringFlowId (generated; no persisted occurrence)
    /// </summary>
    public required Guid SourceId { get; init; }
    public required string Label { get; init; }
    public required Money Amount { get; init; }
    public required Domain.Aggregates.TransactionDirection Direction { get; init; }
    /// <summary>
    /// The signed delta applied to the balance:
    /// Credit → +Amount; Debit → -Amount.
    /// </summary>
    public required Money BalanceDelta { get; init; }
    /// <summary>True if this item was already realized (actual); false if still planned/projected.</summary>
    public required bool IsActual { get; init; }
}

public enum ForecastItemSource
{
    /// <summary>A posted, pending, matched, or reconciled actual transaction.</summary>
    ActualTransaction,
    /// <summary>A pre-existing planned occurrence from the Money Plan.</summary>
    PlannedOccurrence,
    /// <summary>An occurrence freshly expanded from a recurring flow schedule.</summary>
    ExpandedOccurrence,
}

/// <summary>Aggregate household balance on a given date.</summary>
public sealed class AggregateForecastPoint
{
    public required DateOnly Date { get; init; }
    public required Money Balance { get; init; }
}

/// <summary>The minimum projected balance for a single account.</summary>
public sealed class AccountLowWaterMark
{
    public required AccountId AccountId { get; init; }
    public required Money MinBalance { get; init; }
    public required DateOnly Date { get; init; }
}

/// <summary>The aggregate minimum projected balance across all accounts.</summary>
public sealed class AggregateLowWaterMark
{
    public required Money MinBalance { get; init; }
    public required DateOnly Date { get; init; }
}

/// <summary>Warning that a projected balance will go negative.</summary>
public sealed class OverdraftWarning
{
    public required AccountId AccountId { get; init; }
    /// <summary>The first date the balance is projected to fall below zero.</summary>
    public required DateOnly FirstBreachDate { get; init; }
    public required Money ProjectedBalance { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Goal affordability
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Affordability analysis for a single savings goal.</summary>
public sealed class GoalAffordabilityResult
{
    public required Guid GoalId { get; init; }
    public required string Name { get; init; }
    public required Money TargetAmount { get; init; }
    public required Money CurrentBalance { get; init; }
    public required Money RemainingAmount { get; init; }
    public required bool IsAffordable { get; init; }
    /// <summary>
    /// Earliest date by which the goal is projected to be funded, or null if
    /// the projected surplus never covers the remaining amount.
    /// </summary>
    public DateOnly? AffordableByDate { get; init; }
    /// <summary>
    /// The required monthly contribution to reach the goal by <see cref="GoalAffordabilityInput.TargetDate"/>.
    /// Null if no target date was specified.
    /// </summary>
    public decimal? RequiredMonthlyContribution { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Exceptions
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Thrown when the forecast request contains invalid or inconsistent input.</summary>
public sealed class ForecastInputException : Exception
{
    public ForecastInputException(string message) : base(message) { }
}
