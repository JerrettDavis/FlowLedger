using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IPlannedOccurrenceRepository"/>.
///
/// Occurrences are owned by <see cref="RecurringFlow"/> aggregates (OwnsMany), so all
/// write operations load the full owning flow aggregate.
/// Read operations project into <see cref="PlannedFlowOccurrenceView"/> without loading
/// the full graph.
/// </summary>
internal sealed class PlannedOccurrenceRepository : IPlannedOccurrenceRepository
{
    private readonly FlowLedgerDbContext _db;

    public PlannedOccurrenceRepository(FlowLedgerDbContext db) => _db = db;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PlannedFlowOccurrenceView>> ListPendingAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        // Project from the owned collection — EF Core supports LINQ projections over OwnsMany.
        var results = await _db.RecurringFlows
            .SelectMany(rf => rf.Occurrences
                .Where(o => o.Status == OccurrenceStatus.Pending
                            && o.PlannedDate >= from
                            && o.PlannedDate <= to)
                .Select(o => new
                {
                    rf.Name,
                    Occurrence = o
                }))
            .ToListAsync(ct);

        return results.Select(x => new PlannedFlowOccurrenceView(
            x.Occurrence.PlannedOccurrenceId,
            x.Occurrence.RecurringFlowId,
            x.Name,
            x.Occurrence.AccountId,
            x.Occurrence.PlannedAmount,
            x.Occurrence.Direction,
            x.Occurrence.PlannedDate,
            x.Occurrence.Status))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<PlannedFlowOccurrenceView?> GetByIdAsync(
        PlannedOccurrenceId id,
        CancellationToken ct = default)
    {
        var result = await _db.RecurringFlows
            .SelectMany(rf => rf.Occurrences
                .Where(o => o.Id == id.Value)
                .Select(o => new { rf.Name, Occurrence = o }))
            .FirstOrDefaultAsync(ct);

        if (result is null)
        {
            return null;
        }

        return new PlannedFlowOccurrenceView(
            result.Occurrence.PlannedOccurrenceId,
            result.Occurrence.RecurringFlowId,
            result.Name,
            result.Occurrence.AccountId,
            result.Occurrence.PlannedAmount,
            result.Occurrence.Direction,
            result.Occurrence.PlannedDate,
            result.Occurrence.Status);
    }

    /// <inheritdoc/>
    public async Task<RecurringFlow?> GetOwningFlowAsync(
        PlannedOccurrenceId occurrenceId,
        CancellationToken ct = default)
    {
        // Load the full aggregate (including all occurrences) so the domain model
        // can call MatchActual / Unmatch on the target occurrence.
        return await _db.RecurringFlows
            .Include(rf => rf.Occurrences)
            .FirstOrDefaultAsync(rf => rf.Occurrences.Any(o => o.Id == occurrenceId.Value), ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
