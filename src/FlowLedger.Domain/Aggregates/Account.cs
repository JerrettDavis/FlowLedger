using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Domain.Aggregates;

/// <summary>
/// Account aggregate root. Represents a financial account owned by a tenant.
/// Encapsulates balance invariants: credit/liability accounts may carry negative
/// balances, but asset accounts (checking, savings, cash) must not go below their
/// credit limit (which is 0 for non-credit accounts unless explicitly configured).
///
/// Every balance change raises <see cref="AccountBalanceUpdated"/>.
/// </summary>
public sealed class Account : AggregateRoot
{
    private Guid _id;

    public override Guid Id => _id;
    public AccountId AccountId => AccountId.From(_id);
    public TenantId TenantId { get; }
    public string Name { get; private set; }
    public AccountType AccountType { get; }
    public Money CurrentBalance { get; private set; }
    public Money? CreditLimit { get; private set; }
    public string? Institution { get; private set; }
    public string? ExternalAccountRef { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? LastBalanceConfirmedAt { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Account()
    {
        // EF Core constructor — not for direct use. Fields initialised by EF.
        Name = null!;
        CurrentBalance = null!;
    }

    private Account(
        AccountId id,
        TenantId tenantId,
        string name,
        AccountType accountType,
        Money startingBalance,
        Money? creditLimit,
        string? institution,
        DateTimeOffset createdAt)
    {
        _id = id.Value;
        TenantId = tenantId;
        Name = name;
        AccountType = accountType;
        CurrentBalance = startingBalance;
        CreditLimit = creditLimit;
        Institution = institution;
        CreatedAt = createdAt;
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    public static Account Create(
        TenantId tenantId,
        string name,
        AccountType accountType,
        Money startingBalance,
        Money? creditLimit = null,
        string? institution = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new EmptyStringException(nameof(name));
        }

        if (creditLimit is not null && creditLimit.Currency != startingBalance.Currency)
        {
            throw new CurrencyMismatchException(startingBalance.Currency.Code, creditLimit.Currency.Code);
        }

        var account = new Account(
            AccountId.New(),
            tenantId,
            name.Trim(),
            accountType,
            startingBalance,
            creditLimit,
            institution?.Trim(),
            DateTimeOffset.UtcNow);

        account.RaiseEvent(new AccountConnected(
            account.AccountId,
            account.TenantId,
            accountType));

        return account;
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the current balance, e.g. after an import sync or manual reconciliation.
    /// Raises <see cref="AccountBalanceUpdated"/>.
    /// </summary>
    public void UpdateBalance(Money newBalance)
    {
        if (newBalance.Currency != CurrentBalance.Currency)
        {
            throw new CurrencyMismatchException(CurrentBalance.Currency.Code, newBalance.Currency.Code);
        }

        // Asset-type accounts must not go below zero unless they have an explicit credit limit.
        if (IsAssetAccount && newBalance.IsNegative && CreditLimit is null)
        {
            throw new InvalidBalanceException(
                $"Asset account '{Name}' cannot have a negative balance without an explicit credit limit.");
        }

        var previous = CurrentBalance;
        CurrentBalance = newBalance;
        LastBalanceConfirmedAt = DateTimeOffset.UtcNow;

        RaiseEvent(new AccountBalanceUpdated(AccountId, TenantId, previous, newBalance));
    }

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new EmptyStringException(nameof(newName));
        }

        Name = newName.Trim();
    }

    public void Deactivate()
    {
        IsActive = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsAssetAccount => AccountType is
        AccountType.Checking or
        AccountType.Savings or
        AccountType.Cash or
        AccountType.Investment or
        AccountType.ManualAsset;
}

public enum AccountType
{
    Checking,
    Savings,
    CreditCard,
    Loan,
    Mortgage,
    Investment,
    Cash,
    ManualAsset,
    ManualLiability,
}
