using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

internal sealed class AccountRepository : IAccountRepository
{
    private readonly FlowLedgerDbContext _db;

    public AccountRepository(FlowLedgerDbContext db)
        => _db = db;

    public async Task<Account?> GetByIdAsync(AccountId id, CancellationToken ct = default)
        => await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id.Value, ct);

    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default)
        => await _db.Accounts.Where(a => a.IsActive).ToListAsync(ct);

    public async Task<IReadOnlyList<Account>> ListIncludingInactiveAsync(CancellationToken ct = default)
        => await _db.Accounts.ToListAsync(ct);

    public async Task AddAsync(Account account, CancellationToken ct = default)
        => await _db.Accounts.AddAsync(account, ct);

    public void ExpectVersion(Account account, uint? expectedVersion)
    {
        if (expectedVersion is { } version)
        {
            _db.Entry(account).Property(a => a.RowVersion).OriginalValue = version;
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("This account was changed elsewhere. Reload it and try again.", ex);
        }
    }
}
