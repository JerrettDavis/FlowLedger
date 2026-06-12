using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Fakes;

public sealed class FakeRecurringFlowRepository : IRecurringFlowRepository
{
    private readonly List<RecurringFlow> _store = [];

    public Task<RecurringFlow?> GetByIdAsync(RecurringFlowId id, CancellationToken ct = default)
        => Task.FromResult(_store.FirstOrDefault(f => f.Id == id.Value));

    public Task<IReadOnlyList<RecurringFlow>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecurringFlow>>(_store.Where(f => f.IsActive).ToList());

    public Task AddAsync(RecurringFlow flow, CancellationToken ct = default)
    {
        _store.Add(flow);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public IReadOnlyList<RecurringFlow> All => _store.AsReadOnly();
}
