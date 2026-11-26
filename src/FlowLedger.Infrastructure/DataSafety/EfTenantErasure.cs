using FlowLedger.Application.Features.Users;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Per-tenant erasure (#169, #189). Deletes the tenant's rows from every EF-mapped tenant table (children first, from
/// <see cref="TenantDataCatalog"/>), then from any other table that carries a <c>tenant_id</c> column (the ad-hoc tables such as
/// <c>app_users</c> and <c>app_user_link_codes</c>, discovered from the database catalog so a table nobody listed is still cleared),
/// all in one transaction; then clears stored files and marks the registry row deleted. Hard delete: there is no retention window
/// (owner decision #169 may add one). The household tenant is refused. Idempotent.
/// </summary>
internal sealed class EfTenantErasure(
    FlowLedgerDbContext db,
    Guid protectedTenantId,
    IEnumerable<ITenantFileEraser> fileErasers) : ITenantErasure
{
    // Tables that carry a tenant_id column but are not tenant data to delete here.
    private static readonly HashSet<string> NeverTouch = new(StringComparer.Ordinal) { "__EFMigrationsHistory", "tenants" };

    public async Task<TenantErasureResult> EraseAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new TenantErasureRefusedException("A tenant id is required.");
        }

        if (tenantId == protectedTenantId)
        {
            throw new TenantErasureRefusedException("The household tenant cannot be erased.");
        }

        var catalog = TenantDataCatalog.FromModel(db.Model);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            counts.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Tables other than the EF-mapped ones that carry a tenant_id column (ad-hoc tables), straight from the database catalog.
            var withTenantColumn = await db.Database.SqlQueryRaw<string>(
                "SELECT table_name AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND column_name = 'tenant_id' ORDER BY table_name").ToListAsync(ct);
            var columns = catalog.ToDictionary(c => c.Table, c => c.TenantColumn, StringComparer.Ordinal);
            foreach (var table in withTenantColumn.Where(n => !columns.ContainsKey(n) && !NeverTouch.Contains(n)))
            {
                columns[TenantDataCatalog.Validate(table)] = "tenant_id";
            }

            // Delete children before the tables they reference, using the database's real foreign keys.
            var edges = (await db.Database.SqlQueryRaw<ForeignKeyEdge>(
                "SELECT cc.relname AS \"Child\", pc.relname AS \"Parent\" FROM pg_constraint c " +
                "JOIN pg_class cc ON cc.oid = c.conrelid JOIN pg_class pc ON pc.oid = c.confrelid " +
                "JOIN pg_namespace n ON n.oid = cc.relnamespace WHERE c.contype = 'f' AND n.nspname = 'public'").ToListAsync(ct))
                .Select(e => (e.Child, e.Parent)).ToList();

            foreach (var table in TenantDataCatalog.OrderChildrenFirst(columns.Keys, edges))
            {
                counts[table] = await DeleteAsync(
                    TenantDataCatalog.Quote("public") + "." + TenantDataCatalog.Quote(table), columns[table], tenantId, ct);
            }

            await tx.CommitAsync(ct);
        });

        var files = 0;
        foreach (var eraser in fileErasers)
        {
            files += await eraser.EraseTenantAsync(tenantId, ct);
        }

        await MarkDeletedAsync(tenantId, ct);
        return new TenantErasureResult(tenantId, counts, files, DateTimeOffset.UtcNow);
    }

    // The table and column are quoted identifiers that TenantDataCatalog validated against ^[a-z_][a-z0-9_]*$ (they come from the EF
    // model or the database catalog, never from a request); the tenant id is a parameter.
#pragma warning disable EF1002
    internal sealed record ForeignKeyEdge(string Child, string Parent);

    private async Task<int> DeleteAsync(string qualifiedTable, string tenantColumn, Guid tenantId, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM {qualifiedTable} WHERE {TenantDataCatalog.Quote(tenantColumn)} = @t",
            [new NpgsqlParameter("t", tenantId)], ct);
#pragma warning restore EF1002

    private async Task MarkDeletedAsync(Guid tenantId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(NpgsqlTenantRegistry.CreateSqlText, ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO tenants (id, status, deleted_at) VALUES (@t, 'deleted', now()) " +
            "ON CONFLICT (id) DO UPDATE SET status = 'deleted', deleted_at = COALESCE(tenants.deleted_at, now())",
            [new NpgsqlParameter("t", tenantId)], ct);
    }
}
