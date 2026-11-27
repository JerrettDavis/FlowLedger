using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Observability;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;

namespace FlowLedger.Infrastructure.HostStores;

/// <summary>One security-relevant action: who did what to which target, from where, under which trace.</summary>
/// <param name="Action">Stable dotted name, e.g. <c>connector.link</c>, <c>data.import</c>.</param>
/// <param name="Actor">The authenticated principal, plus the user id when known (<c>name</c> or <c>name/userId</c>).</param>
/// <param name="StatusCode">HTTP status the action ended with (denied and failed attempts are recorded too).</param>
public sealed record AuditEvent(
    string Action,
    string Actor,
    Guid? TenantId,
    string? Target,
    string? RemoteIp,
    string? TraceId,
    int? StatusCode,
    DateTimeOffset OccurredUtc);

/// <summary>Append-only record of sensitive actions. There is deliberately no update or delete path.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>
/// EF-backed <see cref="IAuditLog"/> (<c>audit_events</c>, host-wide, through <see cref="HostDb"/>; part of every snapshot/backup).
/// Rows are only ever inserted by this class.
/// </summary>
internal sealed class EfAuditLog(HostDb host) : IAuditLog
{
    public async Task AppendAsync(AuditEvent e, CancellationToken ct = default)
    {
        FlowLedgerTelemetry.RecordAudit(e.Action);
        await using var db = host.Create();
        db.AuditEvents.Add(AuditEventRecord.Create(
            e.OccurredUtc, e.TenantId, e.Actor, e.Action, Truncate(e.Target, 300), Truncate(e.RemoteIp, 64), Truncate(e.TraceId, 64), e.StatusCode));
        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
