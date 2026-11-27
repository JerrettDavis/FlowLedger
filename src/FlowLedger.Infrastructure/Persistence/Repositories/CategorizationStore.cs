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
        => await db.Transactions.Where(TransactionStatusExtensions.RealTransaction).AnyAsync(t => t.CategoryId == null, ct);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<Transaction>> LoadAsync(int max, CancellationToken ct = default)
        => await db.Transactions.AsNoTracking()
            .OrderByDescending(t => t.EffectiveDate)
            .ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Transaction>> LoadByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var rows = new List<Transaction>(ids.Count);
        foreach (var chunk in ids.Chunk(1000))
        {
            rows.AddRange(await db.Transactions.AsNoTracking().Where(t => chunk.Contains(t.Id)).ToListAsync(ct));
        }

        return rows;
    }

    /// <summary>
    /// Writes rows that were loaded without tracking. Each batch is attached as modified, saved, then detached, so the change tracker
    /// holds at most one batch however many rows the post-sync step touched (the whole history used to stay tracked, #163).
    /// </summary>
    public async Task SaveChangedAsync(IReadOnlyCollection<Transaction> changed, CancellationToken ct = default)
    {
        if (changed.Count == 0)
        {
            await db.SaveChangesAsync(ct); // still flush anything else (e.g. categories) the caller staged
            return;
        }

        foreach (var batch in changed.Chunk(SaveBatchSize))
        {
            db.Transactions.UpdateRange(batch);
            await db.SaveChangesAsync(ct);
            foreach (var t in batch)
            {
                db.Entry(t).State = EntityState.Detached;
            }
        }
    }

    internal const int SaveBatchSize = 500;
}
