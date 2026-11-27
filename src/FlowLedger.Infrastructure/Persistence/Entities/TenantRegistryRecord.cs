namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// A row of the tenants registry (#169, #154): the registry itself, keyed by <see cref="Id"/>, not tenant-owned data. Erasure marks a
/// tenant deleted but keeps its row as the audit record. Host-wide: no query filter; reached only through <see cref="HostDb"/>.
/// </summary>
internal sealed class TenantRegistryRecord
{
    public Guid Id { get; private set; }
    public string Status { get; private set; } = "active";
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset? OrphanedUtc { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    private TenantRegistryRecord() { }
}
