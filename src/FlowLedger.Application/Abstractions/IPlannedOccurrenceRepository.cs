using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Repository for querying <see cref="PlannedFlowOccurrence"/> entities
/// and persisting match state changes on their parent <see cref="RecurringFlow"/> aggregates.
/// </summary>
public interface IPlannedOccurrenceRepository
{
    /// <summary>
    /// Returns all Pending planned occurrences within the given date range
    /// for the current tenant.
    /// </summary>
    Task<IReadOnlyList<PlannedFlowOccurrenceView>> ListPendingAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a single occurrence view by ID (any status).
    /// </summary>
    Task<PlannedFlowOccurrenceView?> GetByIdAsync(
        PlannedOccurrenceId id,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the parent <see cref="RecurringFlow"/> aggregate that owns the occurrence,
    /// so the caller can call <see cref="PlannedFlowOccurrence.MatchActual"/> and save.
    /// </summary>
    Task<RecurringFlow?> GetOwningFlowAsync(
        PlannedOccurrenceId occurrenceId,
        CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Flattened read model for a planned occurrence (avoids loading the full RecurringFlow graph
/// for match-candidate queries).
/// </summary>
public sealed record PlannedFlowOccurrenceView(
    PlannedOccurrenceId PlannedOccurrenceId,
    RecurringFlowId RecurringFlowId,
    string RecurringFlowName,
    AccountId AccountId,
    Money PlannedAmount,
    TransactionDirection Direction,
    DateOnly PlannedDate,
    OccurrenceStatus Status);
