using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class RecurringFlowRepository : IRecurringFlowRepository
{
    private readonly FlowLedgerDbContext _db;

    public RecurringFlowRepository(FlowLedgerDbContext db)
        => _db = db;

    public async Task<RecurringFlow?> GetByIdAsync(RecurringFlowId id, CancellationToken ct = default)
        => await _db.RecurringFlows.FirstOrDefaultAsync(f => f.Id == id.Value, ct);

    public async Task<IReadOnlyList<RecurringFlow>> ListAsync(CancellationToken ct = default)
        => await _db.RecurringFlows.Where(f => f.IsActive).ToListAsync(ct);

    public async Task AddAsync(RecurringFlow flow, CancellationToken ct = default)
        => await _db.RecurringFlows.AddAsync(flow, ct);

    public Task SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
