using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Repository abstraction for the RecurringFlow aggregate.
/// All methods are tenant-scoped via the ambient ITenantContext.
/// </summary>
public interface IRecurringFlowRepository
{
    Task<RecurringFlow?> GetByIdAsync(RecurringFlowId id, CancellationToken ct = default);
    Task<IReadOnlyList<RecurringFlow>> ListAsync(CancellationToken ct = default);
    Task AddAsync(RecurringFlow flow, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
