using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Tests.Fakes;

public sealed class FakeTenantContext : ITenantContext
{
    public static readonly Guid DefaultTenantId = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid DefaultUserId = new("bbbbbbbb-0000-0000-0000-000000000001");

    public Guid TenantId { get; }
    public Guid UserId { get; }

    public FakeTenantContext(Guid? tenantId = null, Guid? userId = null)
    {
        TenantId = tenantId ?? DefaultTenantId;
        UserId = userId ?? DefaultUserId;
    }
}
