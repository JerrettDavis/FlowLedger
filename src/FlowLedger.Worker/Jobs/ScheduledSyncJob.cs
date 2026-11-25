using FlowLedger.Application.Abstractions;
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
/// Error tolerance: transient provider errors are caught, logged, and do NOT crash the
/// worker — Quartz will fire the job again on the next scheduled interval.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ScheduledSyncJob : IJob
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
            var syncService = sp.GetRequiredService<IFinancialSyncService>();

            try
            {
                var result = await syncService.SyncAsync(cancellationToken);
                _logger.LogInformation(
                    "ScheduledSyncJob: tenant {TenantId} sync complete — {AccountsUpserted} accounts, " +
                    "{TransactionsAdded} added, {TransactionsSkipped} skipped.",
                    tenantId, result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped);
                await sp.GetRequiredService<FlowLedger.Application.Features.Integrations.IntegrationService>().DispatchAsync(
                    FlowLedger.Application.Features.Integrations.IntegrationEvents.SyncCompleted,
                    new { accounts = result.AccountsUpserted, transactionsAdded = result.TransactionsAdded },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is FlowLedger.Application.Abstractions.SyncThrottledException
                                          or FlowLedger.Application.Abstractions.SyncAlreadyRunningException)
            {
                _logger.LogInformation("ScheduledSyncJob: tenant {TenantId} skipped — {Reason}", tenantId, ex.Message);
            }
            catch (FlowLedger.Integrations.Abstractions.RateLimitedProviderException ex)
            {
                _logger.LogWarning("ScheduledSyncJob: provider rate-limited tenant {TenantId} ({Message}); retrying next interval.", tenantId, ex.Message);
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
