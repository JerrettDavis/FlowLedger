using FlowLedger.Infrastructure.Observability;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

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
/// Postgres-backed <see cref="IAuditLog"/> (<c>audit_events</c>, created on first use like the other ad hoc tables, so it is part
/// of every snapshot/backup and needs no EF migration). Rows are only ever inserted by this class.
/// </summary>
public sealed class NpgsqlAuditLog(string connectionString) : IAuditLog
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS audit_events (
            id bigserial PRIMARY KEY,
            occurred_utc timestamptz NOT NULL,
            tenant_id uuid NULL,
            actor text NOT NULL,
            action text NOT NULL,
            target text NULL,
            remote_ip text NULL,
            trace_id text NULL,
            status_code int NULL
        );
        CREATE INDEX IF NOT EXISTS ix_audit_events_time ON audit_events (occurred_utc);
        CREATE INDEX IF NOT EXISTS ix_audit_events_tenant_time ON audit_events (tenant_id, occurred_utc);
        """;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public async Task AppendAsync(AuditEvent e, CancellationToken ct = default)
    {
        FlowLedgerTelemetry.RecordAudit(e.Action);
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO audit_events (occurred_utc, tenant_id, actor, action, target, remote_ip, trace_id, status_code) " +
            "VALUES (@t, @tenant, @actor, @action, @target, @ip, @trace, @status)", conn);
        cmd.Parameters.AddWithValue("t", e.OccurredUtc);
        cmd.Parameters.AddWithValue("tenant", (object?)e.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actor", e.Actor);
        cmd.Parameters.AddWithValue("action", e.Action);
        cmd.Parameters.AddWithValue("target", (object?)Truncate(e.Target, 300) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ip", (object?)Truncate(e.RemoteIp, 64) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("trace", (object?)Truncate(e.TraceId, 64) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", (object?)e.StatusCode ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        if (!_initialised)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (!_initialised)
                {
                    await using var create = new NpgsqlCommand(CreateSqlText, conn);
                    await create.ExecuteNonQueryAsync(ct);
                    _initialised = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        return conn;
    }
}
