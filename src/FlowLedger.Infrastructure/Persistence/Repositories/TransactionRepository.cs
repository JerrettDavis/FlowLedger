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
        CancellationToken ct = default,
        bool everydayOnly = false,
        string? search = null)
    {
        var query = _db.Transactions.AsNoTracking();

        if (accountId.HasValue)
        {
            // Explicitly drilling into one account always shows it, even a hidden holding.
            query = query.Where(t => EF.Property<AccountId>(t, "AccountId") == accountId.Value);
        }
        else if (everydayOnly)
        {
            query = await ExcludeHiddenAsync(query, ct);
        }

        if (from.HasValue)
        {
            query = query.Where(t => t.EffectiveDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(t => t.EffectiveDate <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(t => t.Description.ToLower().Contains(term)
                || (t.MerchantName != null && t.MerchantName.ToLower().Contains(term))
                || (t.Notes != null && t.Notes.ToLower().Contains(term)));
        }

        return await query
            .OrderByDescending(t => t.EffectiveDate)
            .ThenByDescending(t => t.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Default (unfiltered) views leave out transactions of disabled accounts and of accounts the user hides
    /// (holdings / "hide transactions"). Selecting an account explicitly bypasses this.
    /// </summary>
    private async Task<IQueryable<Transaction>> ExcludeHiddenAsync(IQueryable<Transaction> query, CancellationToken ct)
    {
        var hidden = await _db.Accounts.AsNoTracking()
            .Where(a => !a.IsActive || a.HideTransactions || a.Mode == AccountMode.Holding)
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (hidden.Count == 0)
        {
            return query;
        }

        var hiddenIds = hidden.Select(AccountId.From).ToList();
        return query.Where(t => !hiddenIds.Contains(EF.Property<AccountId>(t, "AccountId")));
    }

    public async Task<IReadOnlyList<Transaction>> ListForAccountsAsync(
        IReadOnlyCollection<AccountId> accountIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var disabled = await _db.Accounts.AsNoTracking().Where(a => !a.IsActive).Select(a => a.Id).ToListAsync(ct);
        var ids = accountIds.Where(i => !disabled.Contains(i.Value)).ToList();
        return await _db.Transactions.AsNoTracking()
            .Where(t => ids.Contains(EF.Property<AccountId>(t, "AccountId")))
            .Where(t => t.EffectiveDate >= from && t.EffectiveDate <= to)
            .OrderByDescending(t => t.EffectiveDate)
            .ThenByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public Task<int> CountAsync(CancellationToken ct = default) => _db.Transactions.CountAsync(ct);

    public async Task<IReadOnlyList<Transaction>> ListAllAsync(int max, CancellationToken ct = default)
        => await _db.Transactions.AsNoTracking()
            .OrderByDescending(t => t.EffectiveDate)
            .ThenByDescending(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Take(max)
            .ToListAsync(ct);

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

        // Rows that were merged away as duplicates stay "existing", so re-importing the same file cannot bring them back.
        var merged = await DuplicateStore.TombstonedAsync(_db, list, ct);
        merged.UnionWith(existing);
        return merged;
    }

    public async Task AddAsync(Transaction transaction, CancellationToken ct = default)
        => await _db.Transactions.AddAsync(transaction, ct);

    public async Task AddRangeAsync(IEnumerable<Transaction> transactions, CancellationToken ct = default)
        => await _db.Transactions.AddRangeAsync(transactions, ct);

    public Task SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
