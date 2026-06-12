using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Tests.Helpers;

/// <summary>
/// Deterministic <see cref="ITenantContext"/> for use in integration tests.
/// Allows injecting a specific tenant and optionally a second tenant to test isolation.
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
