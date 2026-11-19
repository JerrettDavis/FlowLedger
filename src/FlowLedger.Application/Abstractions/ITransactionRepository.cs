using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Abstractions;

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
        bool everydayOnly = false);

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
