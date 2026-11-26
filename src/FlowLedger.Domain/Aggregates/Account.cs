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
    public AccountType AccountType { get; private set; }
    public Money CurrentBalance { get; private set; }
    public Money? CreditLimit { get; private set; }
    public string? Institution { get; private set; }
    public string? ExternalAccountRef { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? LastBalanceConfirmedAt { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>Standard accounts feed every view; Holdings (investments, property) are tracked for balance and net worth only.</summary>
    public AccountMode Mode { get; private set; } = AccountMode.Standard;

    /// <summary>When true the account's transactions stay out of the everyday views (transactions, dashboard, spending, reports) unless filtered to it.</summary>
    public bool HideTransactions { get; private set; }

    /// <summary>True when the account's transactions should be left out of default (unfiltered) views.</summary>
    public bool TransactionsHidden => HideTransactions || Mode == AccountMode.Holding;

    /// <summary>True once the user set the type by hand; provider sync must then leave the type alone.</summary>
    public bool TypeLockedByUser { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token (Postgres <c>xmin</c>, maintained by the database, no schema column). An update made against
    /// a stale copy of the row fails instead of silently overwriting a newer edit or sync write (#157).
    /// </summary>
    public uint RowVersion { get; private set; }

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
        string? institution = null,
        TimeProvider? time = null)
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
            Now(time));

        // Investments are tracked as holdings by default: their buy/sell/dividend lines are noise in everyday views.
        if (accountType == AccountType.Investment)
        {
            account.Mode = AccountMode.Holding;
        }

        account.RaiseEvent(new AccountConnected(
            account.AccountId,
            account.TenantId,
            accountType), account.CreatedAt);

        return account;
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates the current balance, e.g. after an import sync or manual reconciliation.
    /// Raises <see cref="AccountBalanceUpdated"/>.
    /// </summary>
    public void UpdateBalance(Money newBalance, TimeProvider? time = null)
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

        SetBalance(newBalance, time);
    }

    /// <summary>
    /// Applies a balance reported by a provider. A provider balance is a fact about the real account, so the
    /// "assets cannot go negative" input guard of <see cref="UpdateBalance"/> does not apply: an overdrawn
    /// checking account really is negative, and refusing it would leave a stale positive balance behind.
    /// </summary>
    public void ApplyProviderBalance(Money newBalance, TimeProvider? time = null)
    {
        if (newBalance.Currency != CurrentBalance.Currency)
        {
            throw new CurrencyMismatchException(CurrentBalance.Currency.Code, newBalance.Currency.Code);
        }

        SetBalance(newBalance, time);
    }

    private void SetBalance(Money newBalance, TimeProvider? time)
    {
        var previous = CurrentBalance;
        CurrentBalance = newBalance;
        var now = Now(time);
        LastBalanceConfirmedAt = now;

        RaiseEvent(new AccountBalanceUpdated(AccountId, TenantId, previous, newBalance), now);
    }

    /// <summary>
    /// Corrects the account type (e.g. a provider that cannot report types guessed wrong). Liability and
    /// asset accounts are treated differently by forecasts and net worth, so a wrong type causes false
    /// overdraft alerts. No-op when unchanged.
    /// </summary>
    public bool ChangeType(AccountType newType)
    {
        // A type the user chose by hand is never overwritten by provider inference.
        if (TypeLockedByUser || newType == AccountType)
        {
            return false;
        }

        // Same invariant as UpdateBalance: an asset account cannot hold a negative balance without a credit limit.
        if (IsAssetType(newType) && CurrentBalance.IsNegative && CreditLimit is null)
        {
            return false;
        }

        AccountType = newType;
        return true;
    }

    /// <summary>
    /// User-initiated type correction. Unlike <see cref="ChangeType"/> (provider inference) it always applies and
    /// pins the type (<see cref="TypeLockedByUser"/>) so later syncs never overwrite it, even when the type is unchanged.
    /// Liability balances are stored as a positive amount owed, so a negative balance moved onto a liability type is
    /// flipped. Returns true when the type itself changed.
    /// </summary>
    /// <exception cref="InvalidBalanceException">An asset type was requested for a negative balance with no credit limit.</exception>
    public bool SetTypeManually(AccountType newType)
    {
        if (IsAssetType(newType) && CurrentBalance.IsNegative && CreditLimit is null)
        {
            throw new InvalidBalanceException(
                $"Account '{Name}' has a negative balance, so it cannot be an asset account ({newType}) without a credit limit.");
        }

        var changed = newType != AccountType;
        if (IsLiabilityType(newType) && CurrentBalance.IsNegative)
        {
            var previous = CurrentBalance;
            CurrentBalance = CurrentBalance.Abs();
            RaiseEvent(new AccountBalanceUpdated(AccountId, TenantId, previous, CurrentBalance));
        }

        AccountType = newType;
        TypeLockedByUser = true;
        return changed;
    }

    /// <summary>Releases a manual type pin so provider inference may update the type again.</summary>
    public void ReleaseTypeLock() => TypeLockedByUser = false;

    public static bool IsLiabilityType(AccountType type) =>
        type is AccountType.CreditCard or AccountType.Loan or AccountType.Mortgage or AccountType.ManualLiability;

    private static bool IsAssetType(AccountType type) =>
        type is AccountType.Checking or AccountType.Savings or AccountType.Cash
            or AccountType.Investment or AccountType.ManualAsset;

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

    public void Activate()
    {
        IsActive = true;
    }

    /// <summary>Applies the user's tracking preferences in one step.</summary>
    public void ApplySettings(bool isActive, AccountMode mode, bool hideTransactions)
    {
        IsActive = isActive;
        Mode = mode;
        HideTransactions = hideTransactions;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsAssetAccount => IsAssetType(AccountType);
}

/// <summary>How an account participates in reporting.</summary>
public enum AccountMode
{
    /// <summary>A normal account: transactions appear in every view and report.</summary>
    Standard,

    /// <summary>A tracked holding (e.g. brokerage, retirement): balance and history count toward net worth, but its transactions are hidden from everyday views.</summary>
    Holding,
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
