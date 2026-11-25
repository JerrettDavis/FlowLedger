using FlowLedger.Application.Features.Strategies;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Per-tenant user-entered account rates (deposit APY / loan APR and optional months remaining). Created on first use like
/// the other ad-hoc tables, so it needs no EF migration; it is part of every snapshot/backup/export.
/// </summary>
public sealed class NpgsqlAccountRateStore : IAccountRateStore
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS account_rates (
            tenant_id uuid NOT NULL,
            account_id uuid NOT NULL,
            rate_percent numeric(7,3) NOT NULL CHECK (rate_percent >= 0 AND rate_percent <= 100),
            remaining_months int NULL CHECK (remaining_months IS NULL OR (remaining_months >= 1 AND remaining_months <= 600)),
            updated_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, account_id)
        );
        """;

    private readonly string _connectionString;

    public NpgsqlAccountRateStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<AccountRate>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var list = new List<AccountRate>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT account_id, rate_percent, remaining_months FROM account_rates WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AccountRate(r.GetGuid(0), r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetInt32(2)));
        }

        return list;
    }

    public async Task UpsertAsync(Guid tenantId, AccountRate rate, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO account_rates (tenant_id, account_id, rate_percent, remaining_months) VALUES (@t, @a, @r, @m) " +
            "ON CONFLICT (tenant_id, account_id) DO UPDATE SET rate_percent = EXCLUDED.rate_percent, " +
            "remaining_months = EXCLUDED.remaining_months, updated_utc = now()", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", rate.AccountId);
        cmd.Parameters.AddWithValue("r", rate.RatePercent);
        cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlTypes.NpgsqlDbType.Integer) { Value = rate.RemainingMonths is { } m ? m : DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(Guid tenantId, Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM account_rates WHERE tenant_id = @t AND account_id = @a", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", accountId);
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
