using FlowLedger.Application.Features.Strategies;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>
/// Per-tenant account rates through EF (#87). The interface still takes a tenant id; it must be the current tenant, otherwise the
/// call fails loudly instead of silently reading or writing another tenant's rows.
/// </summary>
internal sealed class EfAccountRateStore(FlowLedgerDbContext db, ITenantContext tenant) : IAccountRateStore
{
    private void Guard(Guid tenantId)
    {
        if (tenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("Account rates can only be accessed for the current tenant.");
        }
    }

    public async Task<IReadOnlyList<AccountRate>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        Guard(tenantId);
        var rows = await db.AccountRates.AsNoTracking().ToListAsync(ct);
        return rows.Select(r => new AccountRate(r.AccountId, r.RatePercent, r.RemainingMonths)).ToList();
    }

    public async Task UpsertAsync(Guid tenantId, AccountRate rate, CancellationToken ct = default)
    {
        Guard(tenantId);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO account_rates (tenant_id, account_id, rate_percent, remaining_months)
               VALUES ({tenantId}, {rate.AccountId}, {rate.RatePercent}, {rate.RemainingMonths})
               ON CONFLICT (tenant_id, account_id) DO UPDATE SET rate_percent = EXCLUDED.rate_percent,
               remaining_months = EXCLUDED.remaining_months, updated_utc = now()", ct);
    }

    public async Task RemoveAsync(Guid tenantId, Guid accountId, CancellationToken ct = default)
    {
        Guard(tenantId);
        await db.AccountRates.Where(r => r.AccountId == accountId).ExecuteDeleteAsync(ct);
    }
}
