using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Fakes;

public sealed class FakeCategoryRepository : ICategoryRepository
{
    private readonly List<Category> _store = [];

    public Task<Category?> GetByIdAsync(CategoryId id, CancellationToken ct = default)
        => Task.FromResult(_store.FirstOrDefault(c => c.Id == id.Value));

    public Task<IReadOnlyList<Category>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Category>>(_store.ToList());

    public Task AddAsync(Category category, CancellationToken ct = default)
    {
        _store.Add(category);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Category category, CancellationToken ct = default)
    {
        _store.Remove(category);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public IReadOnlyList<Category> All => _store.AsReadOnly();
}
