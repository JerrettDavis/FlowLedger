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

    public async Task<Transaction?> FindTrackedAsync(Guid id, CancellationToken ct = default)
        => await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<Transaction>> LoadTrackedUncategorizedOrAutoAsync(int max, CancellationToken ct = default)
        => await db.Transactions
            .Where(t => t.CategoryId == null || t.CategorySource == CategorySource.Auto)
            .OrderByDescending(t => t.EffectiveDate)
            .ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Transaction>> LoadTrackedAutoCategorizedAsync(int max, CancellationToken ct = default)
        => await db.Transactions
            .Where(t => t.CategoryId != null && t.CategorySource == CategorySource.Auto)
            .OrderByDescending(t => t.EffectiveDate)
            .ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<bool> AnyUncategorizedAsync(CancellationToken ct = default)
        => await db.Transactions.AnyAsync(t => t.CategoryId == null
            && t.Status != TransactionStatus.Planned && t.Status != TransactionStatus.Skipped, ct);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}
