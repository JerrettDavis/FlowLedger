using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Explicit "no tenant" scope for code that legitimately works across tenants: design-time
/// migrations, the restore drill and admin tooling. Using it is a visible, greppable choice
/// instead of the old silent <c>null</c> that disabled every query filter (#141).
/// It must never be registered as the request-scoped <see cref="ITenantContext"/>.
/// </summary>
public sealed class UnscopedTenantContext : ITenantContext
{
    public static readonly UnscopedTenantContext Instance = new();

    private UnscopedTenantContext()
    {
    }

    public Guid TenantId =>
        throw new InvalidOperationException("This is an unscoped (all-tenants) context; it has no tenant id.");

    public Guid UserId => Guid.Empty;
}
