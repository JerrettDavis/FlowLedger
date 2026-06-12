using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Fakes;

/// <summary>In-memory fake for unit testing transaction handlers.</summary>
public sealed class FakeTransactionRepository : ITransactionRepository
{
    private readonly List<Transaction> _store = [];

    public Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken ct = default)
        => Task.FromResult(_store.FirstOrDefault(t => t.Id == id.Value));

    public Task<IReadOnlyList<Transaction>> ListAsync(
        AccountId? accountId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var query = _store.AsEnumerable();
        if (accountId.HasValue)
        {
            query = query.Where(t => t.AccountId == accountId.Value);
        }

        if (from.HasValue)
        {
            query = query.Where(t => t.EffectiveDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(t => t.EffectiveDate <= to.Value);
        }

        var result = query.Skip(skip).Take(take).ToList();
        return Task.FromResult<IReadOnlyList<Transaction>>(result);
    }

    public Task<HashSet<string>> GetExistingFingerprintsAsync(IEnumerable<string> fingerprints, CancellationToken ct = default)
    {
        var set = fingerprints.ToHashSet();
        var existing = _store
            .Where(t => t.Fingerprint != null && set.Contains(t.Fingerprint.Value))
            .Select(t => t.Fingerprint!.Value)
            .ToHashSet();
        return Task.FromResult(existing);
    }

    public Task AddAsync(Transaction transaction, CancellationToken ct = default)
    {
        _store.Add(transaction);
        return Task.CompletedTask;
    }

    public Task AddRangeAsync(IEnumerable<Transaction> transactions, CancellationToken ct = default)
    {
        _store.AddRange(transactions);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public IReadOnlyList<Transaction> All => _store.AsReadOnly();
}
