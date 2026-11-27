using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
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

    public Task<IReadOnlyList<AccountDto>> HandleAsync(CancellationToken ct = default) => HandleAsync(false, ct);

    public async Task<IReadOnlyList<AccountDto>> HandleAsync(bool includeInactive, CancellationToken ct = default)
    {
        var accounts = includeInactive ? await _repo.ListIncludingInactiveAsync(ct) : await _repo.ListAsync(ct);
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
    private readonly TimeProvider _clock;

    public CreateAccountHandler(IAccountRepository repo, ITenantContext tenant, TimeProvider clock)
    {
        _repo = repo;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<AccountDto> HandleAsync(CreateAccountRequest request, CancellationToken ct = default)
    {
        TenantLimits.EnsureRoom((await _repo.ListIncludingInactiveAsync(ct)).Count, 1, TenantLimits.MaxAccounts, "accounts");
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
            _clock,
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

    public async Task<AccountDto?> HandleAsync(Guid id, UpdateAccountRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        var account = await _repo.GetByIdAsync(AccountId.From(id), ct);
        if (account is null)
        {
            return null;
        }

        _repo.ExpectVersion(account, expectedVersion);
        account.Rename(request.Name);
        await _repo.SaveChangesAsync(ct);

        return AccountMapper.ToDto(account);
    }
}

/// <summary>
/// Corrects an account's type by hand and pins it so provider sync never reverts it. Everything that depends on the type
/// is read live (net worth, forecast liquidity, credit-card alerts, spending/insight transfer detection), except the
/// auto-assigned "Transfer" categories, which are persisted: those are cleared and re-derived so card/loan payments become
/// (or stop being) transfers under the new type. Categories the user chose by hand are never touched.
/// </summary>
public sealed class ChangeAccountTypeHandler
{
    private readonly IAccountRepository _repo;
    private readonly CategorizationService _categorization;

    public ChangeAccountTypeHandler(IAccountRepository repo, CategorizationService categorization)
    {
        _repo = repo;
        _categorization = categorization;
    }

    /// <exception cref="Domain.Exceptions.InvalidBalanceException">The type is incompatible with the balance.</exception>
    public async Task<AccountDto?> HandleAsync(Guid id, ChangeAccountTypeRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        var account = await _repo.GetByIdAsync(AccountId.From(id), ct);
        if (account is null)
        {
            return null;
        }

        var newType = Enum.Parse<AccountType>(request.AccountType, ignoreCase: true);
        _repo.ExpectVersion(account, expectedVersion);
        var changed = account.SetTypeManually(newType);
        await _repo.SaveChangesAsync(ct);

        if (changed)
        {
            await _categorization.RecategorizeAfterAccountTypeChangeAsync(ct);
        }

        return AccountMapper.ToDto(account);
    }
}

/// <summary>Changes an account's tracking settings: enabled, mode (standard/holding) and hidden transactions.</summary>
public sealed class UpdateAccountSettingsHandler(IAccountRepository repo)
{
    public async Task<AccountDto?> HandleAsync(Guid id, AccountSettingsRequest request, uint? expectedVersion = null, CancellationToken ct = default)
    {
        var account = await repo.GetByIdAsync(AccountId.From(id), ct);
        if (account is null)
        {
            return null;
        }

        repo.ExpectVersion(account, expectedVersion);
        account.ApplySettings(request.IsActive, Enum.Parse<AccountMode>(request.Mode, ignoreCase: true), request.HideTransactions);
        await repo.SaveChangesAsync(ct);
        return AccountMapper.ToDto(account);
    }
}

/// <summary>Deactivates an account (soft delete).</summary>
public sealed class DeactivateAccountHandler
{
    private readonly IAccountRepository _repo;

    public DeactivateAccountHandler(IAccountRepository repo)
        => _repo = repo;

    public async Task<bool> HandleAsync(Guid id, uint? expectedVersion = null, CancellationToken ct = default)
    {
        var account = await _repo.GetByIdAsync(AccountId.From(id), ct);
        if (account is null)
        {
            return false;
        }

        _repo.ExpectVersion(account, expectedVersion);
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
        a.LastBalanceConfirmedAt,
        a.TypeLockedByUser,
        a.Mode.ToString(),
        a.HideTransactions,
        a.RowVersion);
}
