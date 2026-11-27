using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Optional column filters for a transaction list (#72), applied by the store before paging and counting.
/// Amount bounds are on the unsigned amount (a 25.00 debit and a 25.00 credit both count as 25).
/// </summary>
public sealed record TransactionFilter(
    Guid? CategoryId = null,
    bool UncategorizedOnly = false,
    Domain.Aggregates.TransactionStatus? Status = null,
    decimal? MinAmount = null,
    decimal? MaxAmount = null)
{
    public bool IsEmpty => CategoryId is null && !UncategorizedOnly && Status is null && MinAmount is null && MaxAmount is null;

    /// <summary>In-memory form of the same predicate, for stores and fakes that do not translate to SQL.</summary>
    public bool Matches(Domain.Aggregates.Transaction t)
        => (CategoryId is null || t.CategoryId?.Value == CategoryId)
        && (!UncategorizedOnly || t.CategoryId is null)
        && (Status is null || t.Status == Status)
        && (MinAmount is null || t.Amount.Amount >= MinAmount)
        && (MaxAmount is null || t.Amount.Amount <= MaxAmount);
}

/// <summary>Column a transaction list can be ordered by on the server (#209). Date is the default and the tie-break for the others.</summary>
public enum TransactionSortKey
{
    Date,
    Merchant,
    Description,
    Amount,
}

/// <summary>
/// Repository abstraction for the Transaction aggregate.
/// All methods are tenant-scoped via the ambient ITenantContext.
/// </summary>
public interface ITransactionRepository
{
    Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken ct = default);

    /// <summary>Total transactions stored for the current tenant (used for the per-tenant quota).</summary>
    async Task<int> CountAsync(CancellationToken ct = default) => (await ListAllAsync(int.MaxValue, ct)).Count;

    /// <summary>Transactions for any of the given accounts in a date window (account filter applied by the store).</summary>
    async Task<IReadOnlyList<Transaction>> ListForAccountsAsync(
        IReadOnlyCollection<AccountId> accountIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var ids = accountIds.ToHashSet();
        return (await ListAsync(null, from, to, 0, int.MaxValue, ct)).Where(t => ids.Contains(t.AccountId)).ToList();
    }

    Task<IReadOnlyList<Transaction>> ListAsync(
        AccountId? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        bool everydayOnly = false,
        string? search = null,
        TransactionSortKey sort = TransactionSortKey.Date,
        bool descending = true,
        TransactionFilter? filter = null);

    /// <summary>
    /// How many transactions match the same filters as <see cref="ListAsync"/> (ignoring paging), so a grid can show
    /// "1-50 of 1,234" and page on the server. Stores should override this with a COUNT query.
    /// </summary>
    async Task<int> CountMatchingAsync(
        AccountId? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default,
        bool everydayOnly = false,
        string? search = null,
        TransactionFilter? filter = null)
        => (await ListAsync(accountId, from, to, 0, int.MaxValue, ct, everydayOnly, search, filter: filter)).Count;

    /// <summary>
    /// A position in the date-ordered list: the row after which the next page starts. Keyset paging reads the next page from here with an
    /// index range instead of an OFFSET that rescans (and, under concurrent inserts, skips or repeats) the rows before it (#160).
    /// </summary>
    public sealed record TransactionKeyset(DateOnly EffectiveDate, DateTimeOffset CreatedAt, Guid Id);

    /// <summary>
    /// The page of up to <paramref name="take"/> transactions that follows <paramref name="after"/> (null = the first page) in the same
    /// date order as <see cref="ListAsync"/> (<paramref name="descending"/> true: newest first), with the same filters. Stores should
    /// override with a keyset query; the default pages the full list in memory, which is correct but loads it.
    /// </summary>
    async Task<IReadOnlyList<Transaction>> ListAfterAsync(
        TransactionKeyset? after, AccountId? accountId, DateOnly? from, DateOnly? to, int take, bool descending, bool everydayOnly, string? search,
        CancellationToken ct = default, TransactionFilter? filter = null)
    {
        var all = await ListAsync(accountId, from, to, 0, int.MaxValue, ct, everydayOnly, search, TransactionSortKey.Date, descending, filter);
        var start = after is null ? 0 : all.ToList().FindIndex(t => t.Id == after.Id) + 1;
        return all.Skip(start).Take(take).ToList();
    }

    // search: case-insensitive match on description, merchant name or notes, applied by the store before paging.

    // everydayOnly: when no account is given, leave out transactions of disabled accounts and of hidden accounts
    // (holdings / "hide transactions"). Pass an account id to drill into one regardless.

    /// <summary>
    /// Read-only snapshot of up to <paramref name="max"/> transactions in a stable total order
    /// (newest first, ties broken by id). One query, so analytics never see a page-boundary duplicate or gap.
    /// </summary>
    Task<IReadOnlyList<Transaction>> ListAllAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// Returns the fingerprint values already stored for the given set of fingerprints
    /// (for deduplication during import).
    /// </summary>
    Task<HashSet<string>> GetExistingFingerprintsAsync(
        IEnumerable<string> fingerprints,
        CancellationToken ct = default);

    Task AddAsync(Transaction transaction, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<Transaction> transactions, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
