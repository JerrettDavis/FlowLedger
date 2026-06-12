using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Fakes;

/// <summary>In-memory fake for unit testing import and matching handlers.</summary>
public sealed class FakePlannedOccurrenceRepository : IPlannedOccurrenceRepository
{
    private readonly List<RecurringFlow> _flows = [];

    /// <summary>Seed a recurring flow (with its occurrences) for tests.</summary>
    public void Seed(RecurringFlow flow) => _flows.Add(flow);

    public Task<IReadOnlyList<PlannedFlowOccurrenceView>> ListPendingAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        var results = _flows
            .SelectMany(rf => rf.Occurrences
                .Where(o => o.Status == OccurrenceStatus.Pending
                            && o.PlannedDate >= from
                            && o.PlannedDate <= to)
                .Select(o => new PlannedFlowOccurrenceView(
                    o.PlannedOccurrenceId,
                    o.RecurringFlowId,
                    rf.Name,
                    o.AccountId,
                    o.PlannedAmount,
                    o.Direction,
                    o.PlannedDate,
                    o.Status)))
            .ToList();

        return Task.FromResult<IReadOnlyList<PlannedFlowOccurrenceView>>(results);
    }

    public Task<PlannedFlowOccurrenceView?> GetByIdAsync(
        PlannedOccurrenceId id,
        CancellationToken ct = default)
    {
        var result = _flows
            .SelectMany(rf => rf.Occurrences
                .Where(o => o.PlannedOccurrenceId == id)
                .Select(o => new PlannedFlowOccurrenceView(
                    o.PlannedOccurrenceId,
                    o.RecurringFlowId,
                    rf.Name,
                    o.AccountId,
                    o.PlannedAmount,
                    o.Direction,
                    o.PlannedDate,
                    o.Status)))
            .FirstOrDefault();

        return Task.FromResult(result);
    }

    public Task<RecurringFlow?> GetOwningFlowAsync(
        PlannedOccurrenceId occurrenceId,
        CancellationToken ct = default)
    {
        var flow = _flows.FirstOrDefault(rf =>
            rf.Occurrences.Any(o => o.PlannedOccurrenceId == occurrenceId));
        return Task.FromResult(flow);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
