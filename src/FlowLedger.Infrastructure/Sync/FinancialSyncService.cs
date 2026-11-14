using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
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
/// Cursor persistence: cursors are durably stored per tenant, provider, and provider account
/// via <see cref="ISyncCursorStore"/>. The cursor is persisted after each successful page so
/// that a mid-sync crash resumes from the last committed page rather than re-fetching everything.
/// </summary>
internal sealed class FinancialSyncService : IFinancialSyncService
{
    private readonly IFinancialDataProvider _provider;
    private readonly FlowLedgerDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ISyncCursorStore _cursorStore;
    private readonly ILogger<FinancialSyncService> _logger;

    public FinancialSyncService(
        IFinancialDataProvider provider,
        FlowLedgerDbContext db,
        ITenantContext tenant,
        ISyncCursorStore cursorStore,
        ILogger<FinancialSyncService> logger)
    {
        _provider = provider;
        _db = db;
        _tenant = tenant;
        _cursorStore = cursorStore;
        _logger = logger;
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        var record = await GetOrProvisionConnectionAsync(tenantId, ct);
        return record.MemberProviderId;
    }

    /// <summary>
    /// Returns the persisted provider connection for this tenant, provisioning one (a billable
    /// provider call) ONLY if none exists. Re-provisioning on every sync would create an orphan
    /// provider user/member per run.
    /// </summary>
    private async Task<ProviderConnectionRecord> GetOrProvisionConnectionAsync(
        TenantId tenantId, CancellationToken ct, bool allowProvision = true)
    {
        var existing = await _db.ProviderConnections
            .FirstOrDefaultAsync(c => c.ProviderName == _provider.ProviderName, ct);
        if (existing is not null)
        {
            return existing;
        }

        if (!allowProvision)
        {
            // Unattended syncs (scheduler, webhooks) must never create billable provider resources.
            throw new NeedsUserActionProviderException(
                $"No {_provider.ProviderName} connection exists for this tenant. Connect an institution first " +
                "(POST /api/integrations/mx/connect-token); syncs never provision implicitly for real providers.");
        }

        _logger.LogInformation("Provisioning provider connection for tenant {TenantId} via {Provider}",
            tenantId, _provider.ProviderName);
        var memberRef = await _provider.BeginConnectionAsync(tenantId, ct);

        // Persist immediately: if the process dies after this point the member is never re-created.
        var record = ProviderConnectionRecord.Create(
            _tenant.TenantId, _provider.ProviderName, memberRef.ProviderId,
            memberRef.InstitutionName, memberRef.Status.ToString(), DateTimeOffset.UtcNow);
        await _db.ProviderConnections.AddAsync(record, ct);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var tenantId = TenantId.From(_tenant.TenantId);
        _logger.LogInformation("Starting sync for tenant {TenantId} via {Provider}", tenantId, _provider.ProviderName);

        // Step 1: reuse the persisted member (provision only on the very first sync).
        var memberRef = await GetOrProvisionConnectionAsync(
            tenantId, ct, allowProvision: _provider.ProviderName == "Simulated");

        // Persist the real member id once the placeholder resolves, so later syncs skip ListMembers.
        if (_provider is IMemberIdResolver resolver)
        {
            var resolved = await resolver.TryResolveMemberProviderIdAsync(memberRef.MemberProviderId, ct);
            if (resolved is not null && resolved != memberRef.MemberProviderId)
            {
                memberRef.ResolveMember(resolved, DateTimeOffset.UtcNow);
                await _db.SaveChangesAsync(ct);
            }
        }

        // Step 2: fetch provider accounts
        var providerAccounts = await _provider.GetAccountsAsync(memberRef.MemberProviderId, ct);
        _logger.LogDebug("Provider returned {Count} accounts", providerAccounts.Count);

        int accountsUpserted = 0;
        int transactionsAdded = 0;
        int transactionsSkipped = 0;

        // Step 3: upsert domain accounts; build a map so recurring flow seeding can resolve
        // provider account ids → domain Account objects.
        var providerIdToAccount = new Dictionary<string, Account>(StringComparer.Ordinal);
        foreach (var pa in providerAccounts)
        {
            var domainAccount = await UpsertAccountAsync(pa, tenantId, ct);
            accountsUpserted++;
            providerIdToAccount[pa.ProviderId] = domainAccount;

            // Append-only history: CurrentBalance is overwritten each sync, the snapshot is not.
            await _db.BalanceSnapshots.AddAsync(AccountBalanceSnapshotRecord.Create(
                _tenant.TenantId, domainAccount.AccountId.Value, _provider.ProviderName, pa.ProviderId,
                pa.Balance.Amount, pa.CurrencyCode, DateTimeOffset.UtcNow), ct);

            // Step 4: sync transactions for this account
            var (added, skipped) = await SyncTransactionsAsync(domainAccount, pa.ProviderId, tenantId, ct);
            transactionsAdded += added;
            transactionsSkipped += skipped;
        }

        // Step 5: seed recurring flows from the simulated provider (idempotent — skips existing).
        var recurringFlowsAdded = await SeedRecurringFlowsAsync(providerIdToAccount, tenantId, ct);

        // Flush all outstanding changes once after the full loop
        await _db.SaveChangesAsync(ct);

        var result = new SyncResult(accountsUpserted, transactionsAdded, transactionsSkipped, recurringFlowsAdded);
        _logger.LogInformation(
            "Sync complete: {AccountsUpserted} accounts, {TxAdded} transactions added, {TxSkipped} skipped, {RfAdded} recurring flows added",
            result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped, result.RecurringFlowsAdded);

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
        var accountType = this.MapProviderAccountType(pa.AccountType);
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

        // Load durable cursor from the store; returns empty string if no prior sync
        var storedCursorValue = await _cursorStore.GetAsync(_provider.ProviderName, providerAccountId, ct);
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

                // Batch-load existing rows (tracked) so pending rows can be settled in place.
                var existingRows = await _db.Transactions
                    .Where(t => t.Fingerprint != null && candidateFingerprints.Contains(t.Fingerprint.Value))
                    .ToListAsync(ct);
                var existingByFp = existingRows.ToDictionary(t => t.Fingerprint!.Value);

                // Pending rows that may have been replaced by a posted record under a new provider id.
                var settleCandidates = await _db.Transactions
                    .Where(t => t.AccountId == account.AccountId && t.Status == TransactionStatus.Pending
                        && t.Source == TransactionSource.MxAggregation)
                    .ToListAsync(ct);

                var toInsert = new List<Transaction>();
                var settled = 0;
                foreach (var x in fingerprintMap)
                {
                    if (existingByFp.TryGetValue(x.Fingerprint.Value, out var row))
                    {
                        // Same provider id seen before: pending -> posted transition in place.
                        if (!x.ProviderTx.IsPending && row.Settle(x.ProviderTx.PostedDate))
                        {
                            settled++;
                        }
                        continue;
                    }

                    if (!x.ProviderTx.IsPending)
                    {
                        // Posted record with a new id: absorb the matching pending row (same amount
                        // and direction, pending date within a week before posting) instead of duplicating.
                        var amount = Math.Abs(x.ProviderTx.Amount.Amount);
                        var direction = x.ProviderTx.Amount.Amount < 0m
                            ? TransactionDirection.Debit : TransactionDirection.Credit;
                        // Only absorb on an unambiguous match; with 0 or 2+ candidates insert instead
                        // (a duplicate is recoverable, a wrong merge is silent data loss).
                        var matches = settleCandidates
                            .Where(t => t.Status == TransactionStatus.Pending
                                && t.Direction == direction
                                && t.Amount.Amount == amount
                                && t.EffectiveDate <= x.ProviderTx.PostedDate
                                && t.EffectiveDate >= x.ProviderTx.PostedDate.AddDays(-7))
                            .Take(2)
                            .ToList();
                        var pending = matches.Count == 1 ? matches[0] : null;
                        if (pending is not null && pending.Settle(x.ProviderTx.PostedDate, x.Fingerprint))
                        {
                            settleCandidates.Remove(pending);
                            settled++;
                            continue;
                        }
                    }

                    toInsert.Add(MapToTransaction(x.ProviderTx, account.AccountId, tenantId, x.Fingerprint));
                }

                if (toInsert.Count > 0)
                {
                    await _db.Transactions.AddRangeAsync(toInsert, ct);
                    added += toInsert.Count;
                }
                if (settled > 0)
                {
                    _logger.LogInformation("Settled {Count} pending transactions on {Account}", settled, providerAccountId);
                }
                skipped += page.Items.Count - toInsert.Count - settled;
            }

            cursor = page.NextCursor;
            hasMore = page.HasMore;

            // Persist cursor after each successful page so a mid-sync crash resumes
            // from the last committed position rather than re-fetching from the beginning.
            await _cursorStore.SetAsync(_provider.ProviderName, providerAccountId, cursor.Value, ct);
        }

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

    /// <summary>
    /// Seeds recurring flows for the simulated provider. Idempotent: flows with the same
    /// Name on the same account are skipped (not duplicated on repeated sync runs).
    /// Returns the number of new flows created.
    /// </summary>
    private async Task<int> SeedRecurringFlowsAsync(
        Dictionary<string, Account> providerIdToAccount,
        TenantId tenantId,
        CancellationToken ct)
    {
        // Recurring flow seeding is only performed for the simulated provider.
        if (_provider is not SimulatedFinancialDataProvider)
        {
            return 0;
        }

        if (providerIdToAccount.Count == 0)
        {
            return 0;
        }

        // Determine the provider account ids for checking (first CHECKING-type account) and
        // credit card (first CREDIT-type account) so seeds can be resolved to domain accounts.
        var checkingEntry = providerIdToAccount
            .FirstOrDefault(kv => kv.Value.AccountType == AccountType.Checking);
        var creditEntry = providerIdToAccount
            .FirstOrDefault(kv => kv.Value.AccountType == AccountType.CreditCard);

        if (checkingEntry.Value is null)
        {
            _logger.LogWarning("Simulated recurring flow seeding skipped: no Checking account found.");
            return 0;
        }

        var seeds = SimulatedDataFactory.GetRecurringFlowSeeds(
            checkingEntry.Key,
            creditEntry.Value is not null ? creditEntry.Key : checkingEntry.Key);

        // Load existing recurring flow names for this tenant to de-duplicate.
        var existingNames = await _db.RecurringFlows
            .Select(rf => rf.Name)
            .ToListAsync(ct);
        var existingNameSet = new HashSet<string>(existingNames, StringComparer.Ordinal);

        int added = 0;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var seed in seeds)
        {
            // Skip if a flow with this name already exists (idempotency guard).
            if (existingNameSet.Contains(seed.Name))
            {
                continue;
            }

            // Resolve domain account; skip seeds whose provider account wasn't synced.
            if (!providerIdToAccount.TryGetValue(seed.ProviderAccountId, out var account))
            {
                // Do not log the raw provider account ID — it is an account-scoped
                // identifier that should not appear in log output (financial app PII policy).
                // The seed name provides enough context for diagnostics.
                _logger.LogWarning(
                    "Skipping recurring flow seed '{Name}': associated provider account not found in synced set.",
                    seed.Name);
                continue;
            }

            var direction = seed.Direction == "Credit"
                ? TransactionDirection.Credit
                : TransactionDirection.Debit;

            var amount = new Money(seed.Amount, new Currency(seed.CurrencyCode));

            var pattern = BuildRecurrencePattern(seed);

            var flow = RecurringFlow.Create(
                tenantId,
                account.AccountId,
                seed.Name,
                amount,
                direction,
                pattern,
                startDate: today,
                endDate: null,
                amountModel: AmountModel.Fixed,
                counterparty: seed.Counterparty);

            await _db.RecurringFlows.AddAsync(flow, ct);
            existingNameSet.Add(seed.Name); // guard against duplicates within the same batch
            added++;
        }

        _logger.LogInformation("Recurring flow seeding: {Added} new flows created.", added);
        return added;
    }

    private static RecurrencePattern BuildRecurrencePattern(SimulatedRecurringFlowSeed seed)
    {
        return seed.FrequencyName switch
        {
            "Monthly" => RecurrencePattern.Monthly(seed.DayOfMonth ?? 1),
            "EveryNWeeks" => RecurrencePattern.EveryNWeeks(
                seed.IntervalWeeks ?? 2,
                Enum.Parse<DayOfWeek>(seed.AnchorDayOfWeek ?? nameof(DayOfWeek.Friday))),
            "Weekly" => RecurrencePattern.Weekly(
                Enum.Parse<DayOfWeek>(seed.AnchorDayOfWeek ?? nameof(DayOfWeek.Monday))),
            "TwiceMonthly" => RecurrencePattern.TwiceMonthly(
                seed.DayOfMonth ?? 1,
                seed.IntervalWeeks ?? 15), // IntervalWeeks reused as secondDay for TwiceMonthly seeds
            _ => RecurrencePattern.Monthly(1),
        };
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
            _logger.LogWarning(
                "Unknown provider account type '{ProviderType}'; defaulting to Checking.",
                providerType);
        }

        return mapped;
    }
}
