namespace FlowLedger.Application.Features.Users;

/// <summary>What revoking a tenant's provider-side data did for one connector (#189).</summary>
public enum ProviderRevocationStatus
{
    /// <summary>The provider deleted the tenant's user, connections and credentials.</summary>
    Revoked,

    /// <summary>The provider held nothing for the tenant.</summary>
    NothingToRevoke,

    /// <summary>The provider has no API to delete an end user; the person must revoke access at the provider themselves.</summary>
    NotSupported,

    /// <summary>The provider call failed. Erasure still went ahead (best effort); the failure is logged for the operator.</summary>
    Failed,
}

/// <summary>Outcome of revoking one connector. <see cref="Connector"/> is a connector id (never request text).</summary>
public sealed record ProviderRevocationResult(string Connector, ProviderRevocationStatus Status);

/// <summary>
/// Port: asks every enabled connector whose API supports it to drop what it holds for the tenant (bank connections, stored credentials).
/// Best effort by contract: it never throws for a provider failure, it reports it.
/// </summary>
public interface ITenantProviderRevocation
{
    Task<IReadOnlyList<ProviderRevocationResult>> RevokeAllAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>Result of <see cref="DeleteMyDataHandler"/>: either the erasure with the provider outcomes, or why nothing was done.</summary>
public sealed record DeleteMyDataOutcome(
    TenantErasureResult? Erasure, IReadOnlyList<ProviderRevocationResult> Providers, string? Refusal, bool ConfirmationMismatch)
{
    public bool Done => Erasure is not null;
}

/// <summary>
/// Owner self-service erasure (#189): the signed-in owner of a tenant deletes their OWN tenant's data. The caller must type
/// <see cref="ConfirmationPhrase"/> exactly. Provider-side data is revoked first (best effort, so a provider outage never traps a person's
/// data), then every row and file of the tenant is erased through <see cref="ITenantErasure"/>, which refuses the household tenant.
/// The tenant id comes from the request's tenant context, never from the body, so one tenant can only ever erase itself.
/// </summary>
internal sealed class DeleteMyDataHandler(ITenantProviderRevocation revocation, ITenantErasure erasure)
{
    /// <summary>The exact text the owner must type to confirm.</summary>
    public const string ConfirmationPhrase = "DELETE MY DATA";

    public async Task<DeleteMyDataOutcome> HandleAsync(Guid tenantId, string? confirmation, CancellationToken ct = default)
    {
        if (!string.Equals(confirmation?.Trim(), ConfirmationPhrase, StringComparison.Ordinal))
        {
            return new DeleteMyDataOutcome(null, [], $"Type \"{ConfirmationPhrase}\" to confirm.", ConfirmationMismatch: true);
        }

        // Refuse before telling any provider anything: the household tenant must never lose its provider connections here.
        if (erasure.RefusalFor(tenantId) is { } refusal)
        {
            return new DeleteMyDataOutcome(null, [], refusal, ConfirmationMismatch: false);
        }

        try
        {
            var providers = await revocation.RevokeAllAsync(tenantId, ct);
            var result = await erasure.EraseAsync(tenantId, ct);
            return new DeleteMyDataOutcome(result, providers, null, ConfirmationMismatch: false);
        }
        catch (TenantErasureRefusedException ex)
        {
            return new DeleteMyDataOutcome(null, [], ex.Message, ConfirmationMismatch: false);
        }
    }
}
