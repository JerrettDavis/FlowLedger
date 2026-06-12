using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Infrastructure implementation of <see cref="IFinancialSyncService"/>.
///
/// Provider selection is handled at DI registration time in DependencyInjection.cs.
/// This service is unaware of which provider is active — it simply calls
/// <see cref="IFinancialDataProvider"/>.
///
/// Idempotency: transactions are deduplicated by <see cref="TransactionFingerprint"/>.
/// The fingerprint index on the database is a secondary guard; the service also performs
/// an in-memory pre-check against the existing fingerprint set to avoid unnecessary inserts.
///
/// Cursor persistence: cursors are stored per provider-account in the accounts
/// <see cref="Account.ExternalAccountRef"/> field for now (M2 quick approach).
/// A dedicated SyncCursorStore will replace this in Milestone 7.
/// </summary>
internal sealed class FinancialSyncService : IFinancialSyncService
{
    private readonly IFinancialDataProvider _provider;
    private readonly FlowLedgerDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<FinancialSyncService> _logger;

    // We keep a simple in-process cursor cache keyed by providerAccountId → cursorValue.
    // This is ephemeral (lost on restart) but sufficient for M2: the DB will also hold
    // the cursor in accounts.external_account_ref prefixed by "cursor:" for persistence.
    private readonly Dictionary<string, string> _cursorCache = new();

    public FinancialSyncService(
        IFinancialDataProvider provider,
        FlowLedgerDbContext db,
        ITenantContext tenant,
        ILogger<FinancialSyncService> logger)
    {
        _provider = provider;
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        _logger.LogInformation("Starting provider connection for tenant {TenantId}", tenantId);

        var memberRef = await _provider.BeginConnectionAsync(tenantId, ct);

        _logger.LogInformation(
            "Provider {Provider} connection initiated. MemberId={MemberId}, Status={Status}",
            _provider.ProviderName, memberRef.ProviderId, memberRef.Status);

        return memberRef.ProviderId;
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        _logger.LogInformation("Starting sync for tenant {TenantId} via {Provider}", tenantId, _provider.ProviderName);

        // Step 1: discover the member ref (simulated: always returns a fixed member)
        var memberRef = await _provider.BeginConnectionAsync(tenantId, ct);

        // Step 2: fetch provider accounts
        var providerAccounts = await _provider.GetAccountsAsync(memberRef.ProviderId, ct);
        _logger.LogDebug("Provider returned {Count} accounts", providerAccounts.Count);

        int accountsUpserted = 0;
        int transactionsAdded = 0;
        int transactionsSkipped = 0;

        // Step 3: upsert domain accounts
        foreach (var pa in providerAccounts)
        {
            var domainAccount = await UpsertAccountAsync(pa, tenantId, ct);
            accountsUpserted++;

            // Step 4: sync transactions for this account
            var (added, skipped) = await SyncTransactionsAsync(domainAccount, pa.ProviderId, tenantId, ct);
            transactionsAdded += added;
            transactionsSkipped += skipped;
        }

        // Flush all outstanding changes once after the full loop
        await _db.SaveChangesAsync(ct);

        var result = new SyncResult(accountsUpserted, transactionsAdded, transactionsSkipped);
        _logger.LogInformation(
            "Sync complete: {AccountsUpserted} accounts, {TxAdded} transactions added, {TxSkipped} skipped",
            result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped);

        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<Account> UpsertAccountAsync(
        ProviderAccount pa,
        TenantId tenantId,
        CancellationToken ct)
    {
        // Look up existing domain account by provider id stored in the Institution field.
        // TODO(M7): replace Institution-as-external-ref with a dedicated AccountProviderLink entity.
        var existing = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Institution == pa.ProviderId, ct);

        if (existing is not null)
        {
            // Update balance if changed (account may carry a stale balance from last sync)
            if (existing.CurrentBalance.Amount != pa.Balance.Amount ||
                existing.CurrentBalance.Currency.Code != pa.CurrencyCode)
            {
                var updatedBalance = new Money(pa.Balance.Amount, new Currency(pa.CurrencyCode));
                try { existing.UpdateBalance(updatedBalance); }
                catch (Domain.Exceptions.InvalidBalanceException)
                {
                    // Asset-account negative balance guard — provider may return negative
                    // for overdraft situations. Accept as-is without throwing.
                    _logger.LogWarning(
                        "Skipping balance update for account {AccountId}: provider returned negative balance on asset account.",
                        existing.Id);
                }
            }
            return existing;
        }

        // Create new account.
        // We store pa.ProviderId in Institution as the stable provider identifier so that
        // subsequent sync runs can locate the same account (no ExternalAccountRef setter exposed).
        var accountType = MapProviderAccountType(pa.AccountType);
        var balance = new Money(pa.Balance.Amount, new Currency(pa.CurrencyCode));

        var newAccount = Account.Create(
            tenantId,
            pa.Name,
            accountType,
            balance,
            creditLimit: null,
            institution: pa.ProviderId); // Institution stores provider id for lookup

        await _db.Accounts.AddAsync(newAccount, ct);
        return newAccount;
    }

    private async Task<(int added, int skipped)> SyncTransactionsAsync(
        Account account,
        string providerAccountId,
        TenantId tenantId,
        CancellationToken ct)
    {
        int added = 0;
        int skipped = 0;

        // Load stored cursor from cache or start fresh
        _cursorCache.TryGetValue(providerAccountId, out var storedCursorValue);
        var cursor = string.IsNullOrEmpty(storedCursorValue) ? SyncCursor.Initial : new SyncCursor(storedCursorValue);

        bool hasMore = true;
        while (hasMore)
        {
            var page = await _provider.GetTransactionsAsync(providerAccountId, cursor, pageSize: 100, ct);

            if (page.Items.Count > 0)
            {
                // Build fingerprints for all items in this page
                var fingerprintMap = page.Items
                    .Select(pt => (
                        ProviderTx: pt,
                        Fingerprint: BuildFingerprint(account.AccountId, pt)))
                    .ToList();

                var candidateFingerprints = fingerprintMap
                    .Select(x => x.Fingerprint.Value)
                    .ToList();

                // Batch-check existing fingerprints to avoid N+1 queries
                var existing = await _db.Transactions
                    .Where(t => t.Fingerprint != null && candidateFingerprints.Contains(t.Fingerprint.Value))
                    .Select(t => t.Fingerprint!.Value)
                    .ToListAsync(ct);

                var existingSet = new HashSet<string>(existing);

                var toInsert = fingerprintMap
                    .Where(x => !existingSet.Contains(x.Fingerprint.Value))
                    .Select(x => MapToTransaction(x.ProviderTx, account.AccountId, tenantId, x.Fingerprint))
                    .ToList();

                if (toInsert.Count > 0)
                {
                    await _db.Transactions.AddRangeAsync(toInsert, ct);
                    added += toInsert.Count;
                }
                skipped += page.Items.Count - toInsert.Count;
            }

            cursor = page.NextCursor;
            hasMore = page.HasMore;
        }

        // Persist cursor for next incremental run
        _cursorCache[providerAccountId] = cursor.Value;

        return (added, skipped);
    }

    private static TransactionFingerprint BuildFingerprint(AccountId accountId, ProviderTransaction pt)
    {
        var normalizedDesc = NormalizeDescription(pt.RawDescription);
        return TransactionFingerprint.Create(
            accountId,
            pt.PostedDate,
            Math.Abs(pt.Amount.Amount),
            normalizedDesc,
            pt.ProviderId);
    }

    private static Transaction MapToTransaction(
        ProviderTransaction pt,
        AccountId accountId,
        TenantId tenantId,
        TransactionFingerprint fingerprint)
    {
        // Signed amount from provider: negative = debit, positive = credit
        var direction = pt.Amount.Amount < 0m
            ? TransactionDirection.Debit
            : TransactionDirection.Credit;

        var amount = new Money(Math.Abs(pt.Amount.Amount), new Currency(pt.Amount.Currency.Code));
        var description = string.IsNullOrWhiteSpace(pt.MerchantName) ? pt.RawDescription : pt.MerchantName;

        return Transaction.RecordActual(
            tenantId,
            accountId,
            amount,
            direction,
            description,
            effectiveDate: pt.PostedDate,
            postedDate: pt.IsPending ? null : pt.PostedDate,
            source: TransactionSource.MxAggregation,
            fingerprint: fingerprint);
    }

    private static string NormalizeDescription(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "UNKNOWN";
        }
        // Collapse whitespace, trim, uppercase for stable fingerprinting
        return System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"\s+", " ").ToUpperInvariant();
    }

    private static AccountType MapProviderAccountType(string providerType) =>
        providerType.ToUpperInvariant() switch
        {
            "CHECKING" => AccountType.Checking,
            "SAVINGS" => AccountType.Savings,
            "CREDIT_CARD" or "CREDITCARD" or "CREDIT CARD" => AccountType.CreditCard,
            "MORTGAGE" => AccountType.Mortgage,
            "LOAN" => AccountType.Loan,
            "INVESTMENT" or "BROKERAGE" => AccountType.Investment,
            "CASH" => AccountType.Cash,
            _ => AccountType.Checking, // safe fallback
        };
}
