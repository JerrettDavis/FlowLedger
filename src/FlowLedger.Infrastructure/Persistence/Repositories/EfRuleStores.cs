using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>Per-tenant categorization rules through EF (#154); tenant-filtered by the DbContext query filter.</summary>
internal sealed class EfCategoryRuleStore(FlowLedgerDbContext db, ITenantContext tenant) : ICategoryRuleStore
{
    public const int MaxRules = 1000;

    public async Task<IReadOnlyList<CategoryRuleDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.CategoryRules.AsNoTracking().OrderBy(r => r.CategoryPath).ThenBy(r => r.Pattern).ToListAsync(ct);
        return rows.Select(r => new CategoryRuleDto(r.Pattern, r.CategoryPath)).ToList();
    }

    public async Task SetAsync(string pattern, string categoryPath, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize writers per tenant so the cap below cannot be raced past; the lock is released at commit.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('category_rules:' || {tenantId.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO category_rules (tenant_id, pattern, category_path)
               SELECT {tenantId}, {pattern}, {categoryPath}
               WHERE (SELECT count(*) FROM category_rules WHERE tenant_id = {tenantId}) < {MaxRules}
                  OR EXISTS (SELECT 1 FROM category_rules WHERE tenant_id = {tenantId} AND pattern = {pattern})
               ON CONFLICT (tenant_id, pattern) DO UPDATE SET category_path = EXCLUDED.category_path, updated_utc = now()", ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> RemoveAsync(string pattern, CancellationToken ct = default)
        => await db.CategoryRules.Where(r => r.Pattern == pattern).ExecuteDeleteAsync(ct) > 0;
}

/// <summary>Per-tenant merchant aliases through EF (#154); tenant-filtered by the DbContext query filter.</summary>
internal sealed class EfMerchantAliasStore(FlowLedgerDbContext db, ITenantContext tenant) : IMerchantAliasStore
{
    public const int MaxAliases = 1000;

    public async Task<IReadOnlyList<MerchantAliasDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.MerchantAliases.AsNoTracking().OrderBy(r => r.Alias).ThenBy(r => r.Pattern).ToListAsync(ct);
        return rows.Select(r => new MerchantAliasDto(r.Pattern, r.Alias)).ToList();
    }

    public async Task SetAsync(string pattern, string alias, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('merchant_aliases:' || {tenantId.ToString()}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO merchant_aliases (tenant_id, pattern, alias)
               SELECT {tenantId}, {pattern}, {alias}
               WHERE (SELECT count(*) FROM merchant_aliases WHERE tenant_id = {tenantId}) < {MaxAliases}
                  OR EXISTS (SELECT 1 FROM merchant_aliases WHERE tenant_id = {tenantId} AND pattern = {pattern})
               ON CONFLICT (tenant_id, pattern) DO UPDATE SET alias = EXCLUDED.alias, updated_utc = now()", ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> RemoveAsync(string pattern, CancellationToken ct = default)
        => await db.MerchantAliases.Where(r => r.Pattern == pattern).ExecuteDeleteAsync(ct) > 0;
}
