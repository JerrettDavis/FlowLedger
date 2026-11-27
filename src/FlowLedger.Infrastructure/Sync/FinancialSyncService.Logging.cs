using FlowLedger.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>Source-generated log messages (CA1848).</summary>
internal sealed partial class FinancialSyncService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning provider connection for tenant {TenantId} via {Provider}")]
    private static partial void LogProvisioningProviderConnectionForTenant(ILogger logger, TenantId tenantId, string? provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read connection health for {Provider}; treating the clean sync as healthy.")]
    private static partial void LogCouldNotReadConnectionHealth(ILogger logger, Exception exception, string? provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record connection health for {Provider}.")]
    private static partial void LogCouldNotRecordConnectionHealth(ILogger logger, Exception exception, string? provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting sync for tenant {TenantId} via {Provider}")]
    private static partial void LogStartingSyncForTenantVia(ILogger logger, TenantId tenantId, string? provider);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Provider returned {Count} accounts")]
    private static partial void LogProviderReturnedAccounts(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not update the provider-account catalog; continuing the sync.")]
    private static partial void LogCouldNotUpdateTheProvider(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sync complete: {AccountsUpserted} accounts, {TxAdded} transactions added, {TxSkipped} skipped, {RfAdded} recurring flows added")]
    private static partial void LogSyncCompleteAccountsTransactionsAdded(ILogger logger, int accountsUpserted, int txAdded, int txSkipped, int rfAdded);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping balance update for account {AccountId}: provider currency differs from the stored account currency.")]
    private static partial void LogSkippingBalanceUpdateForAccount(ILogger logger, Exception exception, Guid accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Account {AccountId} reclassified from provider data.")]
    private static partial void LogAccountReclassifiedFromProviderData(ILogger logger, Guid accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping {Count} zero-amount transaction(s) on one account.")]
    private static partial void LogSkippingZeroAmountTransactionS(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping {Count} transaction(s) whose currency differs from the account currency {Currency}.")]
    private static partial void LogSkippingTransactionSWhoseCurrency(ILogger logger, int count, string currency);

    [LoggerMessage(Level = LogLevel.Information, Message = "Settled {Count} pending transactions during sync")]
    private static partial void LogSettledPendingTransactionsDuringSync(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "The provider dropped {Count} row(s) without a usable date on one account.")]
    private static partial void LogTheProviderDroppedRowS(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retired a stale pending transaction (pending since {Date}).")]
    private static partial void LogRetiredAStalePendingTransaction(ILogger logger, DateOnly date);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync stopped: {Message}")]
    private static partial void LogSyncStopped(ILogger logger, string? message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulated recurring flow seeding skipped: no Checking account found.")]
    private static partial void LogSimulatedRecurringFlowSeedingSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping recurring flow seed '{Name}': associated provider account not found in synced set.")]
    private static partial void LogSkippingRecurringFlowSeedAssociated(ILogger logger, string? name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring flow seeding: {Added} new flows created.")]
    private static partial void LogRecurringFlowSeedingNewFlows(ILogger logger, int added);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown provider account type '{ProviderType}'; defaulting to Checking.")]
    private static partial void LogUnknownProviderAccountTypeDefaulting(ILogger logger, string? providerType);
}
