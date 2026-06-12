using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// A single scheduled occurrence of a <see cref="RecurringFlow"/>. This is the planned
/// side of the planned-vs-actual model (PLAN.md §10.5).
///
/// An occurrence starts as <see cref="OccurrenceStatus.Pending"/>. When an actual
/// transaction is matched to it, it becomes <see cref="OccurrenceStatus.Matched"/> and
/// the variance is recorded. A user can also mark it Skipped.
///
/// This type is an entity owned by its parent RecurringFlow aggregate, not a standalone
/// aggregate root, so it does not have its own event collection.
/// </summary>
public sealed class PlannedFlowOccurrence : IEntity
{
    public PlannedOccurrenceId PlannedOccurrenceId { get; }
    public Guid Id => PlannedOccurrenceId.Value;
    public RecurringFlowId RecurringFlowId { get; }
    public TenantId TenantId { get; }
    public AccountId AccountId { get; }

    public Money PlannedAmount { get; }
    public TransactionDirection Direction { get; }
    public DateOnly PlannedDate { get; }
    public OccurrenceStatus Status { get; private set; } = OccurrenceStatus.Pending;

    /// <summary>Filled in when Status becomes Matched.</summary>
    public TransactionId? MatchedTransactionId { get; private set; }

    /// <summary>Amount variance = Actual - Planned (positive = overspend/underpay).</summary>
    public Money? AmountVariance { get; private set; }

    /// <summary>Date variance in days = Actual.PostedDate - PlannedDate.</summary>
    public int? DateVarianceDays { get; private set; }

    public ConfidenceScore? MatchConfidence { get; private set; }

    internal PlannedFlowOccurrence(
        PlannedOccurrenceId id,
        RecurringFlowId recurringFlowId,
        TenantId tenantId,
        AccountId accountId,
        Money plannedAmount,
        TransactionDirection direction,
        DateOnly plannedDate)
    {
        PlannedOccurrenceId = id;
        RecurringFlowId = recurringFlowId;
        TenantId = tenantId;
        AccountId = accountId;
        PlannedAmount = plannedAmount;
        Direction = direction;
        PlannedDate = plannedDate;
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>
    /// Links an actual transaction to this occurrence and records variance.
    /// Can only be called once; a second match attempt throws.
    /// </summary>
    public void MatchActual(
        TransactionId actualTransactionId,
        Money actualAmount,
        DateOnly actualDate,
        ConfidenceScore confidence)
    {
        if (Status == OccurrenceStatus.Matched)
            throw new OccurrenceAlreadyMatchedException(PlannedOccurrenceId.Value);

        if (Status == OccurrenceStatus.Skipped)
            throw new InvalidStatusTransitionException(
                Status.ToString(), OccurrenceStatus.Matched.ToString(), nameof(PlannedFlowOccurrence));

        if (actualAmount.Currency != PlannedAmount.Currency)
            throw new CurrencyMismatchException(PlannedAmount.Currency.Code, actualAmount.Currency.Code);

        MatchedTransactionId = actualTransactionId;
        AmountVariance = actualAmount - PlannedAmount;
        DateVarianceDays = actualDate.DayNumber - PlannedDate.DayNumber;
        MatchConfidence = confidence;
        Status = OccurrenceStatus.Matched;
    }

    public void Skip()
    {
        if (Status != OccurrenceStatus.Pending)
            throw new InvalidStatusTransitionException(
                Status.ToString(), OccurrenceStatus.Skipped.ToString(), nameof(PlannedFlowOccurrence));
        Status = OccurrenceStatus.Skipped;
    }
}

public enum OccurrenceStatus
{
    Pending,
    Matched,
    Skipped,
}
