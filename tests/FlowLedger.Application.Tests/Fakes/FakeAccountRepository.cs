using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Fakes;

/// <summary>In-memory fake for unit testing account handlers.</summary>
public sealed class FakeAccountRepository : IAccountRepository
{
    private readonly List<Account> _store = [];

    public Task<Account?> GetByIdAsync(AccountId id, CancellationToken ct = default)
        => Task.FromResult(_store.FirstOrDefault(a => a.Id == id.Value));

    public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Account>>(_store.Where(a => a.IsActive).ToList());

    public Task AddAsync(Account account, CancellationToken ct = default)
    {
        _store.Add(account);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
