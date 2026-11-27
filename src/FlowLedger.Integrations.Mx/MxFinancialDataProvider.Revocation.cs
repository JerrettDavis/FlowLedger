using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Mx;

// Tenant erasure (#189): ask MX to forget the user created for this tenant.
public sealed partial class MxFinancialDataProvider
{
    /// <summary>
    /// Deletes the MX user created for the tenant (external id <c>flowledger-{tenant}</c>), which removes its members, credentials and
    /// aggregated data at MX. A tenant that never connected has no MX user: nothing is called to delete.
    /// </summary>
    public async Task<ProviderRevocationOutcome> RevokeTenantAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var externalId = $"flowledger-{tenantId.Value:N}";
        var user = await _client.FindUserByExternalIdAsync(externalId, cancellationToken).ConfigureAwait(false);
        if (user?.Guid is not { Length: > 0 } userGuid)
        {
            return ProviderRevocationOutcome.NothingToRevoke;
        }

        await _client.DeleteUserAsync(userGuid, cancellationToken).ConfigureAwait(false);
        return ProviderRevocationOutcome.Revoked;
    }
}
