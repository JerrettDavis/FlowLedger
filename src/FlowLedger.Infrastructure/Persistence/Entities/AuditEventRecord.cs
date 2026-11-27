namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// One security-relevant action (#193, #154). Append-only and host-wide: <see cref="ActingTenantId"/> is informational (the tenant
/// the action concerned, null for host-level actions), so this is NOT tenant-owned data, carries no query filter, and survives
/// tenant erasure as the audit record. Reached only through <see cref="HostDb"/>.
/// </summary>
internal sealed class AuditEventRecord
{
    public long Id { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public Guid? ActingTenantId { get; private set; }
    public string Actor { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string? Target { get; private set; }
    public string? RemoteIp { get; private set; }
    public string? TraceId { get; private set; }
    public int? StatusCode { get; private set; }

    private AuditEventRecord() { }

    public static AuditEventRecord Create(
        DateTimeOffset occurredUtc, Guid? tenantId, string actor, string action, string? target, string? remoteIp, string? traceId, int? statusCode) =>
        new()
        {
            OccurredUtc = occurredUtc,
            ActingTenantId = tenantId,
            Actor = actor,
            Action = action,
            Target = target,
            RemoteIp = remoteIp,
            TraceId = traceId,
            StatusCode = statusCode,
        };
}
