using FlowLedger.Application.Features.Goals;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Per-tenant savings goals (name, target, amount saved so far, optional target date). Created on first use like the other
/// ad-hoc tables, so it needs no EF migration; it is part of every snapshot/backup/export.
/// </summary>
public sealed class NpgsqlGoalStore : IGoalStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS savings_goals (
            tenant_id uuid NOT NULL,
            id uuid NOT NULL,
            name text NOT NULL,
            target_amount numeric(18,2) NOT NULL CHECK (target_amount > 0),
            saved_amount numeric(18,2) NOT NULL DEFAULT 0 CHECK (saved_amount >= 0),
            target_date date NULL,
            updated_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, id)
        );
        """;

    private readonly string _connectionString;

    public NpgsqlGoalStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<GoalRow>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var list = new List<GoalRow>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, target_amount, saved_amount, target_date FROM savings_goals WHERE tenant_id = @t ORDER BY target_date NULLS LAST, name", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new GoalRow(r.GetGuid(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3),
                r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4))));
        }

        return list;
    }

    public async Task<Guid> UpsertAsync(Guid tenantId, Guid? id, string name, decimal target, decimal saved, DateOnly? targetDate, CancellationToken ct = default)
    {
        var goalId = id ?? Guid.NewGuid();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO savings_goals (tenant_id, id, name, target_amount, saved_amount, target_date) VALUES (@t, @i, @n, @a, @s, @d) " +
            "ON CONFLICT (tenant_id, id) DO UPDATE SET name = EXCLUDED.name, target_amount = EXCLUDED.target_amount, " +
            "saved_amount = EXCLUDED.saved_amount, target_date = EXCLUDED.target_date, updated_utc = now()", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("i", goalId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("a", target);
        cmd.Parameters.AddWithValue("s", saved);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlTypes.NpgsqlDbType.Date) { Value = targetDate is { } d ? d : DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
        return goalId;
    }

    public async Task RemoveAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM savings_goals WHERE tenant_id = @t AND id = @i", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("i", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(CreateSqlText, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
