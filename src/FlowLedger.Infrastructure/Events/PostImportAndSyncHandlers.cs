using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Alerts;
using FlowLedger.Application.Features.Duplicates;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Application.Features.Integrations;
using FlowLedger.Domain.Events;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Events;

// Post-import and post-sync side effects (#92, #175, #180). They used to run inline after the request or job and were lost
// (logged and forgotten) when they failed; as outbox consumers they are retried with backoff and dead-lettered, and a retry
// re-runs only the handler that failed. Delivery is at-least-once, so every handler here is idempotent: merging and sheet
// refresh converge on the same state, Home Assistant tracks what it already sent, and webhooks carry the event id as
// X-FlowLedger-Delivery so a receiver can drop a replay.

/// <summary>Pushes <c>import.completed</c> to subscribed webhooks.</summary>
internal sealed class ImportCompletedWebhookHandler(IntegrationService integrations) : IDomainEventHandler<TransactionsImported>
{
    public async Task HandleAsync(TransactionsImported e, CancellationToken ct = default) =>
        await integrations.DispatchAsync(IntegrationEvents.ImportCompleted, new { imported = e.ImportedCount }, ct, e.EventId);
}

/// <summary>A file often overlaps what the bank already synced: fold the unambiguous overlaps, then refresh the bill sheet.</summary>
internal sealed class PostImportMaintenanceHandler(
    FlowLedgerDbContext db, DuplicateService duplicates, ISheetAutoMarker sheet, ILogger<PostImportMaintenanceHandler> logger)
    : IDomainEventHandler<TransactionsImported>
{
    public async Task HandleAsync(TransactionsImported e, CancellationToken ct = default)
    {
        await ConcurrencyRetry.RunAsync(db, () => duplicates.AutoMergeAsync(ct), logger, "Duplicate merge after import", ct);
        await sheet.RefreshAsync(ct);
    }
}

/// <summary>Pushes <c>sync.completed</c> to subscribed webhooks.</summary>
internal sealed class SyncCompletedWebhookHandler(IntegrationService integrations) : IDomainEventHandler<SyncCompleted>
{
    public async Task HandleAsync(SyncCompleted e, CancellationToken ct = default) =>
        await integrations.DispatchAsync(
            IntegrationEvents.SyncCompleted, new { accounts = e.AccountsUpserted, transactionsAdded = e.TransactionsAdded }, ct, e.EventId);
}

/// <summary>Folds unambiguous duplicates the sync brought in (High confidence only), then refreshes the bill sheet.</summary>
internal sealed class PostSyncMaintenanceHandler(
    FlowLedgerDbContext db, DuplicateService duplicates, ISheetAutoMarker sheet, ILogger<PostSyncMaintenanceHandler> logger)
    : IDomainEventHandler<SyncCompleted>
{
    public async Task HandleAsync(SyncCompleted e, CancellationToken ct = default)
    {
        if (e.TransactionsAdded > 0)
        {
            await ConcurrencyRetry.RunAsync(db, () => duplicates.AutoMergeAsync(ct), logger, "Duplicate merge after sync", ct);
        }

        await sheet.RefreshAsync(ct);
    }
}

/// <summary>Pushes any alert the new data raised (bills, paychecks, overdraft, budgets) to Home Assistant straight away instead of waiting for the hourly job.</summary>
internal sealed class SyncCompletedHomeAssistantHandler(HomeAssistantService homeAssistant) : IDomainEventHandler<SyncCompleted>
{
    public async Task HandleAsync(SyncCompleted e, CancellationToken ct = default) => await homeAssistant.RunAsync(ct);
}

/// <summary>Pushes any alert the new data raised to the webhooks subscribed to <c>alert.raised</c>; each alert goes out once.</summary>
internal sealed class SyncCompletedAlertWebhookHandler(AlertDeliveryService delivery) : IDomainEventHandler<SyncCompleted>
{
    public async Task HandleAsync(SyncCompleted e, CancellationToken ct = default) => await delivery.RunAsync(ct);
}
