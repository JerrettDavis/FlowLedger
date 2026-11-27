using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace FlowLedger.Worker.Jobs;

/// <summary>
/// Quartz job that performs an incremental financial sync for all tenants.
///
/// Tenancy note: in the current milestone the tenant context is provided by the DI
/// registration (dev tenant / simulated provider). Multi-tenant fan-out is a later concern.
///
/// Error tolerance: provider errors are caught and logged, and do NOT crash the worker. Whether a failing
/// connection is retried on the next fire (back-off window, user action pending, retries exhausted) is decided
/// by <see cref="SyncRetryPolicy"/>, never by ad hoc catch blocks here.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class ScheduledSyncJob : IJob
{
    // JobDataMap key used to pass the IServiceProvider from tests.
    internal const string ServiceProviderKey = "ServiceProvider";

    private readonly IServiceProvider _services;
    private readonly ILogger<ScheduledSyncJob> _logger;
    private readonly JobScheduleOptions _options;

    public ScheduledSyncJob(
        IServiceProvider services,
        ILogger<ScheduledSyncJob> logger,
        IOptions<JobScheduleOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "ScheduledSyncJob: disabled via configuration (ScheduledSync:Enabled=false). Skipping.");
            return;
        }

        _logger.LogInformation("ScheduledSyncJob: starting incremental sync.");

        // One DI scope per tenant (DbContext, tenant context, sync service); a failing tenant never blocks the others.
        await FlowLedger.Worker.TenantFanOut.ForEachTenantAsync(_services, _logger, async (sp, tenantId) =>
        {
            // ADR 0018: one shared policy decides whether this tenant's connections may be retried yet (back-off window,
            // waiting for the user, retries exhausted). The sync itself records health; the job only consults the verdict.
            var gate = sp.GetService<SyncAttemptGate>();
            if (gate is not null && await gate.CheckAsync(SyncTrigger.Scheduled, cancellationToken) is { Allowed: false, Blocking: { } blocked })
            {
                _logger.LogInformation("ScheduledSyncJob: tenant {TenantId} skipped — {Reason}", tenantId, blocked.Reason);
                return;
            }

            var syncService = sp.GetRequiredService<IFinancialSyncService>();

            try
            {
                var result = await syncService.SyncAsync(cancellationToken);
                _logger.LogInformation(
                    "ScheduledSyncJob: tenant {TenantId} sync complete — {AccountsUpserted} accounts, " +
                    "{TransactionsAdded} added, {TransactionsSkipped} skipped.",
                    tenantId, result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped);
            }
            catch (Exception ex) when (ex is FlowLedger.Application.Abstractions.SyncThrottledException
                                          or FlowLedger.Application.Abstractions.SyncAlreadyRunningException)
            {
                _logger.LogInformation("ScheduledSyncJob: tenant {TenantId} skipped — {Reason}", tenantId, ex.Message);
            }
            catch (ProviderException ex)
            {
                // Already classified and persisted by the sync service; the next fire consults the retry policy.
                _logger.LogWarning("ScheduledSyncJob: tenant {TenantId} sync failed ({Kind}): {Message}",
                    tenantId, ProviderIssue.FromException(ex).Kind, ex.Message);
            }
            catch (Exception ex)
            {
                // Log and continue — Quartz treats an unhandled exception as a job failure; a graceful
                // log-and-continue is safer for transient provider errors.
                _logger.LogError(ex,
                    "ScheduledSyncJob: sync faulted for tenant {TenantId}. It will retry on the next scheduled interval.", tenantId);
            }
        }, cancellationToken);
    }
}
