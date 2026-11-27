using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Maps a provider-side member/profile id from a verified webhook to the tenant that owns that connection (#150).
/// Deliberately cross-tenant and read-only; it is the only sanctioned way to look a tenant up without a request.
/// </summary>
public interface IWebhookTenantResolver
{
    /// <summary>The owning tenant, or null when the member is unknown or (defensively) claimed by more than one tenant.</summary>
    Task<Guid?> ResolveAsync(ConnectorId connector, string memberProviderId, CancellationToken ct = default);
}
