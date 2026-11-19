using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Per-tenant catalog of provider accounts seen by each connector. Created on first use (no EF migration); included in every backup.</summary>
public sealed class NpgsqlProviderAccountCatalog(string connectionString, ITenantContext tenant) : IProviderAccountCatalog
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS connector_provider_accounts (
            tenant_id uuid NOT NULL,
            connector_id text NOT NULL,
            provider_account_id text NOT NULL,
            name text NOT NULL,
            account_type text NOT NULL,
            balance numeric(18,4) NOT NULL,
            currency text NOT NULL,
            last_seen_utc timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, connector_id, provider_account_id)
        );
        """;

    public async Task UpsertAsync(IReadOnlyCollection<DiscoveredProviderAccount> accounts, CancellationToken ct = default)
    {
        if (accounts.Count == 0)
        {
            return;
        }

        await using var conn = await OpenAsync(ct);
        foreach (var a in accounts)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO connector_provider_accounts (tenant_id, connector_id, provider_account_id, name, account_type, balance, currency, last_seen_utc) " +
                "VALUES (@t, @c, @p, @n, @y, @b, @u, @s) " +
                "ON CONFLICT (tenant_id, connector_id, provider_account_id) DO UPDATE SET name = EXCLUDED.name, " +
                "account_type = EXCLUDED.account_type, balance = EXCLUDED.balance, currency = EXCLUDED.currency, last_seen_utc = EXCLUDED.last_seen_utc", conn);
            cmd.Parameters.AddWithValue("t", tenant.TenantId);
            cmd.Parameters.AddWithValue("c", a.ConnectorId);
            cmd.Parameters.AddWithValue("p", a.ProviderAccountId);
            cmd.Parameters.AddWithValue("n", a.Name);
            cmd.Parameters.AddWithValue("y", a.AccountType);
            cmd.Parameters.AddWithValue("b", a.Balance);
            cmd.Parameters.AddWithValue("u", a.Currency);
            cmd.Parameters.AddWithValue("s", a.LastSeenUtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<DiscoveredProviderAccount>> ListAsync(string? connectorId = null, CancellationToken ct = default)
    {
        var list = new List<DiscoveredProviderAccount>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT connector_id, provider_account_id, name, account_type, balance, currency, last_seen_utc FROM connector_provider_accounts " +
            "WHERE tenant_id = @t AND (@c::text IS NULL OR connector_id = @c) ORDER BY connector_id, name", conn);
        cmd.Parameters.AddWithValue("t", tenant.TenantId);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)connectorId ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new DiscoveredProviderAccount(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        }

        return list;
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
