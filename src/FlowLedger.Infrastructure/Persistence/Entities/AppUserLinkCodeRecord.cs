namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// A one-time code (stored hashed) that lets another login join a tenant (#154). Part of the host-wide user directory: no query filter,
/// reached only through <see cref="HostDb"/>. <see cref="TargetTenantId"/> (column <c>tenant_id</c>) is the tenant the code joins.
/// </summary>
internal sealed class AppUserLinkCodeRecord
{
    public string CodeHash { get; private set; } = string.Empty;
    public Guid TargetTenantId { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset ExpiresUtc { get; private set; }
    public DateTimeOffset? UsedUtc { get; private set; }

    private AppUserLinkCodeRecord() { }

    public static AppUserLinkCodeRecord Create(string codeHash, Guid tenantId, DateTimeOffset expiresUtc) =>
        new() { CodeHash = codeHash, TargetTenantId = tenantId, CreatedUtc = DateTimeOffset.UtcNow, ExpiresUtc = expiresUtc };

    public void MarkUsed() => UsedUtc = DateTimeOffset.UtcNow;
}
