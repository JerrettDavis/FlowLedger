using FlowLedger.Application.Features.Users;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// The tenants registry (#169). Created on first use like the other ad-hoc tables. A tenant used to exist only implicitly as a
/// <c>tenant_id</c> value on rows, so a tenant whose last login was re-pointed by a link code became invisible. Now every tenant
/// has a row; the user directory keeps it in step (active / orphaned) and erasure marks it deleted but never removes it.
/// The key column is <c>id</c> (not <c>tenant_id</c>) because this table is the registry itself, not tenant-owned data.
/// </summary>
internal sealed class NpgsqlTenantRegistry(string connectionString) : ITenantRegistry
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS tenants (
            id uuid PRIMARY KEY,
            status text NOT NULL DEFAULT 'active',
            created_utc timestamptz NOT NULL DEFAULT now(),
            orphaned_utc timestamptz NULL,
            deleted_at timestamptz NULL
        );
        """;

    /// <summary>Idempotent: registers every tenant that already has a login, plus the household tenant. Run after both tables exist.</summary>
    public const string BackfillSqlText = """
        INSERT INTO tenants (id) SELECT DISTINCT tenant_id FROM app_users ON CONFLICT (id) DO NOTHING;
        """;

    private volatile bool _ready;

    public async Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, status, created_utc, orphaned_utc, deleted_at FROM tenants WHERE id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<TenantInfo>> ListAsync(TenantStatus status, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, status, created_utc, orphaned_utc, deleted_at FROM tenants WHERE status = @s ORDER BY created_utc, id", conn);
        cmd.Parameters.AddWithValue("s", ToDb(status));
        var list = new List<TenantInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        return list;
    }

    internal static string ToDb(TenantStatus status) => status.ToString().ToLowerInvariant();

    private static TenantInfo Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        Enum.TryParse<TenantStatus>(r.GetString(1), ignoreCase: true, out var s) ? s : TenantStatus.Active,
        r.GetFieldValue<DateTimeOffset>(2),
        r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4));

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        if (!_ready)
        {
            // Registry reads must work before any login has touched the user directory (first run after upgrade): create both
            // tables and register the tenants that already have logins.
            await using var create = new NpgsqlCommand(string.Join(" ", NpgsqlUserDirectory.CreateSqlText, CreateSqlText, BackfillSqlText), conn);
            await create.ExecuteNonQueryAsync(ct);
            _ready = true;
        }

        return conn;
    }
}
