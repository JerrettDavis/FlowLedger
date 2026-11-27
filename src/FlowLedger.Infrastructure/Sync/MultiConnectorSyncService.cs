using FlowLedger.Application.Abstractions;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Syncs a tenant through every enabled connector it may use (ADR 0010) and merges the results. Each connector runs the ordinary
/// per-provider <see cref="FinancialSyncService"/>, which only touches the accounts linked to (or newly discovered by) that connector.
///
/// Failure isolation: one connector failing never stops the others. If every connector fails the first error is rethrown (so a
/// single-connector install behaves exactly as before); if at least one succeeds the failures are logged and the partial result returned.
/// </summary>
internal sealed partial class MultiConnectorSyncService : IFinancialSyncService
{
    private readonly IServiceProvider _services;
    private readonly IConnectorRegistry _registry;
    private readonly ITenantContext _tenant;
    private readonly ILogger<MultiConnectorSyncService> _logger;
    private readonly Guid _householdTenantId;

    public MultiConnectorSyncService(
        IServiceProvider services,
        IConnectorRegistry registry,
        ITenantContext tenant,
        IConfiguration configuration,
        ILogger<MultiConnectorSyncService> logger)
    {
        _services = services;
        _registry = registry;
        _tenant = tenant;
        _logger = logger;
        _householdTenantId = TenancySettings.Legacy(configuration).LegacyTenantId;
    }

    /// <summary>Connectors enabled on the instance that this tenant may use. Operator-held credentials serve the household tenant only.</summary>
    public static IReadOnlyList<ConnectorDescriptor> UsableFor(IConnectorRegistry registry, Guid tenantId, Guid householdTenantId)
        => registry.Enabled.Where(c => !c.SharedCredentials || tenantId == householdTenantId).ToList();

    public IReadOnlyList<ConnectorDescriptor> Usable => UsableFor(_registry, _tenant.TenantId, _householdTenantId);

    private FinancialSyncService For(ConnectorDescriptor connector)
        => ActivatorUtilities.CreateInstance<FinancialSyncService>(_services, _registry.Resolve(connector.Id));

    public Task<string> ConnectAsync(CancellationToken ct = default)
    {
        // Provisioning a connection is per connector; the demo/primary flow uses the first usable one.
        var usable = Usable;
        var primary = usable.Count > 0 ? usable[0] : throw NoUsableConnector();
        return For(primary).ConnectAsync(ct);
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var connectors = Usable;
        if (connectors.Count == 0)
        {
            return new SyncResult(0, 0, 0, 0); // nothing this tenant may sync (same as the old "unlinked tenant" behaviour)
        }

        var accounts = 0;
        var added = 0;
        var skipped = 0;
        var flows = 0;
        var failures = new List<Exception>();

        foreach (var connector in connectors)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await For(connector).SyncAsync(ct);
                accounts += r.AccountsUpserted;
                added += r.TransactionsAdded;
                skipped += r.TransactionsSkipped;
                flows += r.RecurringFlowsAdded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSyncViaConnectorFailedFor(_logger, ex, connector.Id, _tenant.TenantId);
                failures.Add(ex);
            }
        }

        if (failures.Count == connectors.Count)
        {
            // Nothing succeeded: surface the first error unchanged so callers (UI messages, retries) behave as with one connector.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        return new SyncResult(accounts, added, skipped, flows);
    }

    private static NeedsUserActionProviderException NoUsableConnector() => new NeedsUserActionProviderException(
        "This account has no linked financial institution yet. Per-user provider linking is not available; the configured connectors belong to the household owner.",
        "Ask the household owner, or add accounts manually / via CSV import.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync via connector {Connector} failed for tenant {TenantId}.")]
    private static partial void LogSyncViaConnectorFailedFor(ILogger logger, Exception exception, string? connector, Guid tenantId);
}
