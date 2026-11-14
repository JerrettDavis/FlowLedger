using FlowLedger.Application.Features.Categories;
using FlowLedger.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class CategorizationStore(FlowLedgerDbContext db) : ICategorizationStore
{
    public async Task<IReadOnlyList<Transaction>> LoadTrackedAsync(int max, CancellationToken ct = default)
        => await db.Transactions
            .OrderByDescending(t => t.EffectiveDate)
            .ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}
