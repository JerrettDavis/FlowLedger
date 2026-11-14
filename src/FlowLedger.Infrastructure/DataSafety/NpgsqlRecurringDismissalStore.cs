using FlowLedger.Application.Features.RecurringFlows;
using FlowLedger.SharedKernel;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Remembers which recurring-flow suggestions the user dismissed. Created on first use (IF NOT EXISTS) like the other
/// ad-hoc tables, so it needs no EF migration; backups pick it up automatically because they enumerate every public table.
/// </summary>
public sealed class NpgsqlRecurringDismissalStore(string connectionString, ITenantContext tenant) : IRecurringDismissalStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS recurring_dismissals (
            tenant_id uuid NOT NULL,
            suggestion_key text NOT NULL,
            dismissed_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, suggestion_key)
        );
        """;

    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken ct = default)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT suggestion_key FROM recurring_dismissals WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            keys.Add(r.GetString(0));
        }

        return keys;
    }

    public async Task AddAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO recurring_dismissals (tenant_id, suggestion_key) VALUES (@t, @k) ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.AddWithValue("k", key);
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
