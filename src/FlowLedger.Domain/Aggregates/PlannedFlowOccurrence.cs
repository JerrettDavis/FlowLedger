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
    private Guid _id;
    private Guid _recurringFlowId;
    private Guid _tenantId;
    private Guid _accountId;
    private Guid? _matchedTransactionId;
    private decimal? _matchConfidence;

    public PlannedOccurrenceId PlannedOccurrenceId => PlannedOccurrenceId.From(_id);
    public Guid Id => _id;
    public RecurringFlowId RecurringFlowId => RecurringFlowId.From(_recurringFlowId);
    public TenantId TenantId => Domain.ValueObjects.TenantId.From(_tenantId);
    public AccountId AccountId => Domain.ValueObjects.AccountId.From(_accountId);

    public Money PlannedAmount { get; private set; }
    public TransactionDirection Direction { get; private set; }
    public DateOnly PlannedDate { get; private set; }
    public OccurrenceStatus Status { get; private set; } = OccurrenceStatus.Pending;

    /// <summary>Filled in when Status becomes Matched.</summary>
    public TransactionId? MatchedTransactionId => _matchedTransactionId.HasValue
        ? Domain.ValueObjects.TransactionId.From(_matchedTransactionId.Value)
        : null;

    /// <summary>Amount variance = Actual - Planned (positive = overspend/underpay).</summary>
    public Money? AmountVariance { get; private set; }

    /// <summary>Date variance in days = Actual.PostedDate - PlannedDate.</summary>
    public int? DateVarianceDays { get; private set; }

    public ConfidenceScore? MatchConfidence => _matchConfidence.HasValue
        ? new ConfidenceScore(_matchConfidence.Value)
        : null;

    private PlannedFlowOccurrence()
    {
        // EF Core parameterless constructor — fields initialised by EF.
        // Not for direct use outside of EF hydration.
        PlannedAmount = null!;
    }

    internal PlannedFlowOccurrence(
        PlannedOccurrenceId id,
        RecurringFlowId recurringFlowId,
        TenantId tenantId,
        AccountId accountId,
        Money plannedAmount,
        TransactionDirection direction,
        DateOnly plannedDate)
    {
        _id = id.Value;
        _recurringFlowId = recurringFlowId.Value;
        _tenantId = tenantId.Value;
        _accountId = accountId.Value;
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

        _matchedTransactionId = actualTransactionId.Value;
        AmountVariance = actualAmount - PlannedAmount;
        DateVarianceDays = actualDate.DayNumber - PlannedDate.DayNumber;
        _matchConfidence = confidence.Value;
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
