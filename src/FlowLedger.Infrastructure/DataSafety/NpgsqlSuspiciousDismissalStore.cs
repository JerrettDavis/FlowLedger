using FlowLedger.Application.Features.Dashboard;
using FlowLedger.SharedKernel;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Remembers which flagged transactions the user reviewed and dismissed, with an optional note. Created on first use
/// (IF NOT EXISTS) like the other ad-hoc tables, so it needs no EF migration; backups enumerate every public table.
/// </summary>
public sealed class NpgsqlSuspiciousDismissalStore(string connectionString, ITenantContext tenant) : ISuspiciousDismissalStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS suspicious_dismissals (
            tenant_id uuid NOT NULL,
            transaction_id uuid NOT NULL,
            note text NULL,
            dismissed_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, transaction_id)
        );
        """;

    public async Task<IReadOnlySet<Guid>> ListIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<Guid>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT transaction_id FROM suspicious_dismissals WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            ids.Add(r.GetGuid(0));
        }

        return ids;
    }

    public async Task DismissAsync(Guid transactionId, string? note, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO suspicious_dismissals (tenant_id, transaction_id, note) VALUES (@t, @x, @n)
            ON CONFLICT (tenant_id, transaction_id) DO UPDATE SET note = EXCLUDED.note, dismissed_utc = now()
            """, conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("x", transactionId);
        cmd.Parameters.AddWithValue("n", (object?)note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var create = new NpgsqlCommand(CreateSqlText, conn);
        await create.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
