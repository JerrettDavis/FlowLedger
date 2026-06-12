using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class TransactionRepository : ITransactionRepository
{
    private readonly FlowLedgerDbContext _db;

    public TransactionRepository(FlowLedgerDbContext db)
        => _db = db;

    public async Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken ct = default)
        => await _db.Transactions.FirstOrDefaultAsync(t => t.Id == id.Value, ct);

    public async Task<IReadOnlyList<Transaction>> ListAsync(
        AccountId? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var query = _db.Transactions.AsQueryable();

        if (accountId.HasValue)
        {
            query = query.Where(t => EF.Property<AccountId>(t, "AccountId") == accountId.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(t => t.EffectiveDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(t => t.EffectiveDate <= to.Value);
        }

        return await query
            .OrderByDescending(t => t.EffectiveDate)
            .ThenByDescending(t => t.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<HashSet<string>> GetExistingFingerprintsAsync(
        IEnumerable<string> fingerprints,
        CancellationToken ct = default)
    {
        var list = fingerprints.ToList();
        if (list.Count == 0)
        {
            return [];
        }

        // EF Core owned-entity navigation: Fingerprint.Value is mapped as a shadow-like owned column.
        // We query via EF.Property to reference the mapped column name.
        var existing = await _db.Transactions
            .Where(t => t.Fingerprint != null && list.Contains(t.Fingerprint.Value))
            .Select(t => t.Fingerprint!.Value)
            .ToListAsync(ct);

        return [.. existing];
    }

    public async Task AddAsync(Transaction transaction, CancellationToken ct = default)
        => await _db.Transactions.AddAsync(transaction, ct);

    public async Task AddRangeAsync(IEnumerable<Transaction> transactions, CancellationToken ct = default)
        => await _db.Transactions.AddRangeAsync(transactions, ct);

    public Task SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
