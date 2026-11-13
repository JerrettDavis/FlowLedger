using FlowLedger.SharedKernel;

namespace FlowLedger.Worker;

/// <summary>
/// Tenant for background jobs, which have no HTTP request to resolve one from. Single-tenant for now:
/// the tenant comes from <c>Worker:TenantId</c> and defaults to the same demo tenant the API uses, so
/// scheduled syncs write into the data the UI shows. Replace with per-tenant fan-out for multi-tenancy.
/// </summary>
internal sealed class ConfiguredTenantContext(Guid tenantId) : ITenantContext
{
    public static readonly Guid DemoTenantId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid DemoUserId = new("00000000-0000-0000-0000-000000000002");

    public Guid TenantId { get; } = tenantId;
    public Guid UserId => DemoUserId;
}
