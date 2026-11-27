using FlowLedger.Application.Features.Users;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>
/// Tenant erasure, provider side (#189). For every enabled connector: a connector that runs on operator-held shared credentials never
/// held anything for a non-household tenant (nothing to revoke); a provider that implements <see cref="IProviderTenantRevoker"/> is asked to
/// delete the tenant's user and connections; any other provider has no such API and is reported as not supported. Best effort: a provider
/// failure is logged (connector id and exception only) and reported as <see cref="ProviderRevocationStatus.Failed"/>; it never blocks the erasure.
/// </summary>
internal sealed partial class ProviderTenantRevocation(IConnectorRegistry registry, ILogger<ProviderTenantRevocation> logger) : ITenantProviderRevocation
{
    public async Task<IReadOnlyList<ProviderRevocationResult>> RevokeAllAsync(Guid tenantId, CancellationToken ct = default)
    {
        var results = new List<ProviderRevocationResult>();
        foreach (var connector in registry.Enabled)
        {
            var id = connector.Id.Value;
            if (connector.SharedCredentials)
            {
                results.Add(new ProviderRevocationResult(id, ProviderRevocationStatus.NothingToRevoke));
                continue;
            }

            IFinancialDataProvider provider;
            try
            {
                provider = registry.Resolve(connector.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRevocationFailed(logger, ex, id, tenantId);
                results.Add(new ProviderRevocationResult(id, ProviderRevocationStatus.Failed));
                continue;
            }

            if (provider is not IProviderTenantRevoker revoker)
            {
                LogRevocationNotSupported(logger, id, tenantId);
                results.Add(new ProviderRevocationResult(id, ProviderRevocationStatus.NotSupported));
                continue;
            }

            try
            {
                var outcome = await revoker.RevokeTenantAsync(TenantId.From(tenantId), ct);
                LogRevocationDone(logger, id, tenantId, outcome);
                results.Add(new ProviderRevocationResult(
                    id, outcome == ProviderRevocationOutcome.Revoked ? ProviderRevocationStatus.Revoked : ProviderRevocationStatus.NothingToRevoke));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRevocationFailed(logger, ex, id, tenantId);
                results.Add(new ProviderRevocationResult(id, ProviderRevocationStatus.Failed));
            }
        }

        return results;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider revocation for tenant {TenantId}: connector {Connector} -> {Outcome}")]
    private static partial void LogRevocationDone(ILogger logger, string connector, Guid tenantId, ProviderRevocationOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider revocation for tenant {TenantId}: connector {Connector} has no revoke API; the person must remove access at the provider")]
    private static partial void LogRevocationNotSupported(ILogger logger, string connector, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider revocation for tenant {TenantId}: connector {Connector} failed; erasure continues")]
    private static partial void LogRevocationFailed(ILogger logger, Exception exception, string connector, Guid tenantId);
}
