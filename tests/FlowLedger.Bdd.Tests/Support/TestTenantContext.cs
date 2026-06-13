using FlowLedger.SharedKernel;

namespace FlowLedger.Bdd.Tests.Support;

/// <summary>
/// Deterministic <see cref="ITenantContext"/> for use in BDD integration scenarios.
/// Recreated in-project (do not cross-reference other test projects).
/// </summary>
public sealed class TestTenantContext : ITenantContext
{
    public Guid TenantId { get; }
    public Guid UserId { get; }

    public TestTenantContext(Guid tenantId, Guid? userId = null)
    {
        TenantId = tenantId;
        UserId = userId ?? Guid.NewGuid();
    }

    /// <summary>Creates a context with a new random tenant ID.</summary>
    public static TestTenantContext New() => new(Guid.NewGuid());
}
