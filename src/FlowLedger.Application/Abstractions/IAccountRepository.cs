using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Repository abstraction for the Account aggregate.
/// Implementations live in Infrastructure; consumers depend only on this interface.
/// All methods are tenant-scoped via the ambient ITenantContext.
/// </summary>
public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(AccountId id, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default);

    /// <summary>Every account, including disabled ones (for the management screen).</summary>
    Task<IReadOnlyList<Account>> ListIncludingInactiveAsync(CancellationToken ct = default);
    Task AddAsync(Account account, CancellationToken ct = default);

    /// <summary>
    /// Makes the next save fail with <see cref="ConcurrencyConflictException"/> unless the stored row is still at
    /// <paramref name="expectedVersion"/> (the version the caller last saw, from the account's ETag). Null means the
    /// caller sent no precondition, so the write is unconditional.
    /// </summary>
    void ExpectVersion(Account account, uint? expectedVersion);

    Task SaveChangesAsync(CancellationToken ct = default);
}
