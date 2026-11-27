using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

// Account matching/creation and provider account-type mapping (#103).
internal sealed partial class FinancialSyncService
{
    /// <summary>
    /// Finds (or creates) the domain account for a provider account, honouring per-account connector links (ADR 0010):
    /// a link for (this connector, provider account) wins; otherwise the legacy match on <c>Institution</c> is used and linked;
    /// otherwise a new account is created and linked. Returns null when the matched account belongs to another connector.
    /// </summary>
    private async Task<Account?> UpsertAccountAsync(
        ProviderAccount pa,
        TenantId tenantId,
        CancellationToken ct)
    {
        Account? existing = null;

        if (_links is not null)
        {
            var link = await _links.FindByProviderAccountAsync(_provider.ProviderName, pa.ProviderId, ct);
            if (link is not null)
            {
                existing = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == link.AccountId, ct);
                if (existing is null)
                {
                    await _links.UnlinkAsync(link.AccountId, ct); // stale link: the account was deleted
                }
            }
        }

        if (existing is null)
        {
            // Accounts created before per-account links store the provider account id in Institution.
            existing = await _db.Accounts.FirstOrDefaultAsync(a => a.Institution == pa.ProviderId, ct);
            if (existing is not null && _links is not null)
            {
                var theirs = await _links.FindByAccountAsync(existing.Id, ct);
                if (theirs is null)
                {
                    await _links.LinkAsync(existing.Id, _provider.ProviderName, pa.ProviderId, ct); // lazy backfill
                }
                else if (!string.Equals(theirs.ConnectorId, _provider.ProviderName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(theirs.ProviderAccountId, pa.ProviderId, StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }

        if (existing is not null)
        {
            // A type the user pinned by hand wins over provider inference. The provider only normalises liability
            // balances (positive = owed) for types IT inferred, so re-apply that convention for the pinned type.
            var providerAmount = pa.Balance.Amount;
            if (existing.TypeLockedByUser
                && Account.IsLiabilityType(existing.AccountType)
                && !(TryMapProviderAccountType(pa.AccountType, out var providerType) && Account.IsLiabilityType(providerType)))
            {
                providerAmount = ToOwed(providerAmount);
            }

            // Update balance if changed (account may carry a stale balance from last sync)
            if (existing.CurrentBalance.Amount != providerAmount ||
                existing.CurrentBalance.Currency.Code != pa.CurrencyCode)
            {
                var updatedBalance = new Money(providerAmount, new Currency(pa.CurrencyCode));
                try { existing.ApplyProviderBalance(updatedBalance, _clock); }
                catch (Domain.Exceptions.CurrencyMismatchException ex)
                {
                    // A provider changing an account's currency must not abort the whole sync.
                    LogSkippingBalanceUpdateForAccount(_logger, ex, existing.Id);
                }
            }

            // Providers refine the type over time (SimpleFIN infers it from names). Keep it in step so a card or
            // loan never sits in the liquid-cash / overdraft calculations. Guard rails: only for types the provider
            // actually maps (not the Checking fallback), and never silently downgrade a liability to an asset.
            if (TryMapProviderAccountType(pa.AccountType, out var mappedType)
                && !(Account.IsLiabilityType(existing.AccountType) && !Account.IsLiabilityType(mappedType)))
            {
                var previousType = existing.AccountType;
                if (existing.ChangeType(mappedType))
                {
                    LogAccountReclassifiedFromProviderData(_logger, existing.Id);
                }
            }

            return existing;
        }

        if (!_autoCreateAccounts)
        {
            return null; // catalogued only: the owner links it to an account
        }

        // Create new account.
        // We store pa.ProviderId in Institution as the stable provider identifier so that
        // subsequent sync runs can locate the same account (no ExternalAccountRef setter exposed).
        var accountType = this.MapProviderAccountType(pa.AccountType);
        var balance = new Money(pa.Balance.Amount, new Currency(pa.CurrencyCode));

        var newAccount = Account.Create(
            tenantId,
            pa.Name,
            accountType,
            balance,
            creditLimit: null,
            institution: pa.ProviderId, // Institution stores provider id for lookup
            time: _clock);

        await _db.Accounts.AddAsync(newAccount, ct);
        if (_links is not null)
        {
            // The link row has a DB-level composite foreign key to the account (#146), so the account is committed first. A crash
            // between the two leaves an unlinked account, which the next sync finds by Institution and links again (lazy backfill).
            await _db.SaveChangesAsync(ct);
            await _links.LinkAsync(newAccount.Id, _provider.ProviderName, pa.ProviderId, ct);
        }

        return newAccount;
    }

    /// <summary>
    /// Converts a provider balance to FlowLedger's "owed is positive" convention for a liability the user pinned
    /// by hand. SimpleFIN and Quiltt report what you HAVE (a card owing 200 is -200), so negate once; MX already
    /// reports what you owe. The sign is kept: an overpaid card stays a negative debt (a credit), never flipped.
    /// </summary>
    private decimal ToOwed(decimal providerAmount) =>
        _provider.Traits.HasFlag(ConnectorTraits.ReportsHoldingsSign) ? -providerAmount : providerAmount;

    /// <summary>Maps a canonical provider type token; false (no change) when the type is unknown/unmapped.</summary>
    private static bool TryMapProviderAccountType(string providerType, out AccountType type)
    {
        switch (providerType.ToUpperInvariant())
        {
            case "CHECKING": type = AccountType.Checking; return true;
            case "SAVINGS": type = AccountType.Savings; return true;
            case "CREDIT_CARD" or "CREDITCARD" or "CREDIT CARD" or "CREDIT": type = AccountType.CreditCard; return true;
            case "MORTGAGE": type = AccountType.Mortgage; return true;
            case "LOAN": type = AccountType.Loan; return true;
            case "INVESTMENT" or "BROKERAGE": type = AccountType.Investment; return true;
            case "CASH": type = AccountType.Cash; return true;
            default: type = default; return false;
        }
    }

    private AccountType MapProviderAccountType(string providerType)
    {
        var normalizedType = providerType.ToUpperInvariant();
        var mapped = normalizedType switch
        {
            "CHECKING" => AccountType.Checking,
            "SAVINGS" => AccountType.Savings,
            "CREDIT_CARD" or "CREDITCARD" or "CREDIT CARD" or "CREDIT" => AccountType.CreditCard,
            "MORTGAGE" => AccountType.Mortgage,
            "LOAN" => AccountType.Loan,
            "INVESTMENT" or "BROKERAGE" => AccountType.Investment,
            "CASH" => AccountType.Cash,
            _ => AccountType.Checking, // safe fallback
        };

        if (mapped == AccountType.Checking && normalizedType != "CHECKING")
        {
            LogUnknownProviderAccountTypeDefaulting(_logger, providerType);
        }

        return mapped;
    }
}
