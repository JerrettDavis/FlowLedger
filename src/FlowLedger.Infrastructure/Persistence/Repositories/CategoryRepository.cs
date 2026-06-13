using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class CategoryRepository : ICategoryRepository
{
    private readonly FlowLedgerDbContext _db;

    public CategoryRepository(FlowLedgerDbContext db)
        => _db = db;

    public async Task<Category?> GetByIdAsync(CategoryId id, CancellationToken ct = default)
        => await _db.Categories.FirstOrDefaultAsync(c => c.Id == id.Value, ct);

    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken ct = default)
        => await _db.Categories.OrderBy(c => c.Path).ToListAsync(ct);

    public async Task AddAsync(Category category, CancellationToken ct = default)
        => await _db.Categories.AddAsync(category, ct);

    public Task RemoveAsync(Category category, CancellationToken ct = default)
    {
        _db.Categories.Remove(category);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
