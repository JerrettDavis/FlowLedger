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
        string? search = null,
        TransactionSortKey sort = TransactionSortKey.Date,
        bool descending = true)
    {
        var query = await FilteredAsync(accountId, from, to, everydayOnly, search, ct);

        // Sorted in SQL before paging, so "sort by amount" orders the whole result and not just the page on screen (#209).
        // Date (newest or oldest first by direction) is always the final tie-break, then CreatedAt and Id, so pages never overlap.
        IOrderedQueryable<Transaction> ordered = (sort, descending) switch
        {
            (TransactionSortKey.Merchant, false) => query.OrderBy(t => t.MerchantName ?? t.SuggestedName ?? t.Description),
            (TransactionSortKey.Merchant, true) => query.OrderByDescending(t => t.MerchantName ?? t.SuggestedName ?? t.Description),
            (TransactionSortKey.Description, false) => query.OrderBy(t => t.Description),
            (TransactionSortKey.Description, true) => query.OrderByDescending(t => t.Description),
            (TransactionSortKey.Amount, false) => query.OrderBy(t => t.Amount.Amount),
            (TransactionSortKey.Amount, true) => query.OrderByDescending(t => t.Amount.Amount),
            (_, false) => query.OrderBy(t => t.EffectiveDate),
            _ => query.OrderByDescending(t => t.EffectiveDate),
        };

        if (sort != TransactionSortKey.Date)
        {
            ordered = descending ? ordered.ThenByDescending(t => t.EffectiveDate) : ordered.ThenBy(t => t.EffectiveDate);
        }

        return await ordered
            .ThenByDescending(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Transaction>> ListAfterAsync(
        ITransactionRepository.TransactionKeyset? after, AccountId? accountId, DateOnly? from, DateOnly? to, int take, bool descending, bool everydayOnly,
        string? search, CancellationToken ct = default)
    {
        var query = await FilteredAsync(accountId, from, to, everydayOnly, search, ct);

        // The same total order as ListAsync's date sort: date (direction as asked), then newest-created first, then id.
        if (after is { } k)
        {
            var (date, created, id) = (k.EffectiveDate, k.CreatedAt, k.Id);
            query = descending
                ? query.Where(t => t.EffectiveDate < date
                    || (t.EffectiveDate == date && (t.CreatedAt < created || (t.CreatedAt == created && t.Id.CompareTo(id) > 0))))
                : query.Where(t => t.EffectiveDate > date
                    || (t.EffectiveDate == date && (t.CreatedAt < created || (t.CreatedAt == created && t.Id.CompareTo(id) > 0))));
        }

        var ordered = descending ? query.OrderByDescending(t => t.EffectiveDate) : query.OrderBy(t => t.EffectiveDate);
        return await ordered.ThenByDescending(t => t.CreatedAt).ThenBy(t => t.Id).Take(take).ToListAsync(ct);
    }

    public async Task<int> CountMatchingAsync(
        AccountId? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default,
        bool everydayOnly = false,
        string? search = null)
        => await (await FilteredAsync(accountId, from, to, everydayOnly, search, ct)).CountAsync(ct);

    private async Task<IQueryable<Transaction>> FilteredAsync(
        AccountId? accountId, DateOnly? from, DateOnly? to, bool everydayOnly, string? search, CancellationToken ct)
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
            // ToLower() on the column is deliberate: EF translates it to SQL lower(); string.Contains(string, StringComparison) is not translatable.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(t => t.Description.ToLower().Contains(term)
                || (t.MerchantName != null && t.MerchantName.ToLower().Contains(term))
                || (t.Notes != null && t.Notes.ToLower().Contains(term)));
#pragma warning restore CA1304, CA1311, CA1862
        }

        return query;
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
