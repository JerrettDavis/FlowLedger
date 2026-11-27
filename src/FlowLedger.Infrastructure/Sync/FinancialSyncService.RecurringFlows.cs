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

// Recurring-flow seeding from provider data (#103).
internal sealed partial class FinancialSyncService
{
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
        // Only demo-data connectors seed recurring flows.
        if (_provider is not IRecurringFlowSeedSource seedSource)
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
            LogSimulatedRecurringFlowSeedingSkipped(_logger);
            return 0;
        }

        var seeds = seedSource.GetRecurringFlowSeeds(
            checkingEntry.Key,
            creditEntry.Value is not null ? creditEntry.Key : checkingEntry.Key);

        // Load existing recurring flow names for this tenant to de-duplicate.
        var existingNames = await _db.RecurringFlows
            .Select(rf => rf.Name)
            .ToListAsync(ct);
        var existingNameSet = new HashSet<string>(existingNames, StringComparer.Ordinal);

        int added = 0;
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

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
                LogSkippingRecurringFlowSeedAssociated(_logger, seed.Name);
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
                counterparty: seed.Counterparty,
                time: _clock);

            await _db.RecurringFlows.AddAsync(flow, ct);
            existingNameSet.Add(seed.Name); // guard against duplicates within the same batch
            added++;
        }

        LogRecurringFlowSeedingNewFlows(_logger, added);
        return added;
    }

    private static RecurrencePattern BuildRecurrencePattern(RecurringFlowSeed seed)
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
}
