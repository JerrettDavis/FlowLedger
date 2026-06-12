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
    Task AddAsync(Account account, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
