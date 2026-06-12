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
    public async Task Execute(IJobExecutionContext context)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "ScheduledSyncJob: disabled via configuration (ScheduledSync:Enabled=false). Skipping.");
            return;
        }

        _logger.LogInformation("ScheduledSyncJob: starting incremental sync.");

        // Open a DI scope so scoped services (DbContext, tenant context, sync service)
        // are properly disposed after each job execution.
        await using var scope = _services.CreateAsyncScope();

        var syncService = scope.ServiceProvider.GetRequiredService<IFinancialSyncService>();

        try
        {
            var result = await syncService.SyncAsync(context.CancellationToken);
            _logger.LogInformation(
                "ScheduledSyncJob: sync complete — {AccountsUpserted} accounts, " +
                "{TransactionsAdded} added, {TransactionsSkipped} skipped.",
                result.AccountsUpserted, result.TransactionsAdded, result.TransactionsSkipped);
        }
        catch (Exception ex)
        {
            // Log and return without rethrowing — Quartz considers an unhandled exception a
            // job failure and may invoke misfire handling; a graceful log-and-continue is safer
            // for transient provider errors.
            _logger.LogError(ex,
                "ScheduledSyncJob: sync faulted. The job will retry on the next scheduled interval.");
        }
    }
}
