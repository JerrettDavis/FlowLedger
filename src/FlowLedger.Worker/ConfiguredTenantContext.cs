using FlowLedger.SharedKernel;

namespace FlowLedger.Worker;

/// <summary>
/// Tenant for background jobs, which have no HTTP request to resolve one from. Starts as the configured household
/// tenant (<c>Worker:TenantId</c>, default demo tenant) and is switched per tenant by <see cref="TenantFanOut"/>
/// when a job iterates every user's tenant. Scoped: one instance per job scope, never shared across tenants.
/// </summary>
internal sealed class ConfiguredTenantContext(Guid tenantId) : ITenantContext
{
    public static readonly Guid DemoTenantId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid DemoUserId = new("00000000-0000-0000-0000-000000000002");

    public Guid TenantId { get; private set; } = tenantId;
    public Guid UserId => DemoUserId;

    public void Use(Guid tenant) => TenantId = tenant;
}
