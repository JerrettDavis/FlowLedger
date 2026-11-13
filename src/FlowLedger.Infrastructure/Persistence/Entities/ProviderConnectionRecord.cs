namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// Durable record of the provider-side member/connection created for a tenant.
/// Persisting this is what stops every sync from provisioning a brand-new (billable)
/// provider user + member: the sync reuses <see cref="MemberProviderId"/> instead.
/// </summary>
internal sealed class ProviderConnectionRecord
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public string MemberProviderId { get; private set; } = string.Empty;
    public string Institution { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ProviderConnectionRecord() { }

    public static ProviderConnectionRecord Create(
        Guid tenantId, string providerName, string memberProviderId,
        string institution, string status, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ProviderName = providerName,
        MemberProviderId = memberProviderId,
        Institution = institution,
        Status = status,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void UpdateStatus(string status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}
