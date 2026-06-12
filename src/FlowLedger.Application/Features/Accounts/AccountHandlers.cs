using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Accounts;

/// <summary>Returns all active accounts for the current tenant.</summary>
public sealed class ListAccountsHandler
{
    private readonly IAccountRepository _repo;

    public ListAccountsHandler(IAccountRepository repo)
        => _repo = repo;

    public async Task<IReadOnlyList<AccountDto>> HandleAsync(CancellationToken ct = default)
    {
        var accounts = await _repo.ListAsync(ct);
        return accounts.Select(AccountMapper.ToDto).ToList().AsReadOnly();
    }
}

/// <summary>Returns a single account by ID, or null if not found.</summary>
public sealed class GetAccountHandler
{
    private readonly IAccountRepository _repo;

    public GetAccountHandler(IAccountRepository repo)
        => _repo = repo;

    public async Task<AccountDto?> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _repo.GetByIdAsync(AccountId.From(id), ct);
        return account is null ? null : AccountMapper.ToDto(account);
    }
}

/// <summary>Creates a new account for the current tenant.</summary>
public sealed class CreateAccountHandler
{
    private readonly IAccountRepository _repo;
    private readonly ITenantContext _tenant;

    public CreateAccountHandler(IAccountRepository repo, ITenantContext tenant)
    {
        _repo = repo;
        _tenant = tenant;
    }

    public async Task<AccountDto> HandleAsync(CreateAccountRequest request, CancellationToken ct = default)
    {
        var accountType = Enum.Parse<AccountType>(request.AccountType, ignoreCase: true);
        var currency = new Currency(request.Currency.ToUpperInvariant());
        var balance = new Money(request.StartingBalance, currency);
        Money? creditLimit = request.CreditLimit.HasValue
            ? new Money(request.CreditLimit.Value, currency)
            : null;

        var account = Account.Create(
            TenantId.From(_tenant.TenantId),
            request.Name,
            accountType,
            balance,
            creditLimit,
            request.Institution);

        await _repo.AddAsync(account, ct);
        await _repo.SaveChangesAsync(ct);

        return AccountMapper.ToDto(account);
    }
}

/// <summary>Renames an existing account.</summary>
public sealed class UpdateAccountHandler
{
    private readonly IAccountRepository _repo;

    public UpdateAccountHandler(IAccountRepository repo)
        => _repo = repo;

    public async Task<AccountDto?> HandleAsync(Guid id, UpdateAccountRequest request, CancellationToken ct = default)
    {
        var account = await _repo.GetByIdAsync(AccountId.From(id), ct);
        if (account is null)
        {
            return null;
        }

        account.Rename(request.Name);
        await _repo.SaveChangesAsync(ct);

        return AccountMapper.ToDto(account);
    }
}

/// <summary>Deactivates an account (soft delete).</summary>
public sealed class DeactivateAccountHandler
{
    private readonly IAccountRepository _repo;

    public DeactivateAccountHandler(IAccountRepository repo)
        => _repo = repo;

    public async Task<bool> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var account = await _repo.GetByIdAsync(AccountId.From(id), ct);
        if (account is null)
        {
            return false;
        }

        account.Deactivate();
        await _repo.SaveChangesAsync(ct);
        return true;
    }
}

internal static class AccountMapper
{
    public static AccountDto ToDto(Account a) => new(
        a.Id,
        a.Name,
        a.AccountType.ToString(),
        a.CurrentBalance.Amount,
        a.CurrentBalance.Currency.Code,
        a.Institution,
        a.ExternalAccountRef,
        a.IsActive,
        a.CreatedAt,
        a.LastBalanceConfirmedAt);
}
