using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>Per-tenant catalog of provider accounts seen by each connector, through EF (#154); the tenant query filter scopes reads.</summary>
internal sealed class EfProviderAccountCatalog(FlowLedgerDbContext db, ITenantContext tenant) : IProviderAccountCatalog
{
    public async Task UpsertAsync(IReadOnlyCollection<DiscoveredProviderAccount> accounts, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        foreach (var a in accounts)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO connector_provider_accounts (tenant_id, connector_id, provider_account_id, name, account_type, balance, currency, last_seen_utc)
                   VALUES ({tenantId}, {a.ConnectorId.Value}, {a.ProviderAccountId}, {a.Name}, {a.AccountType}, {a.Balance}, {a.Currency}, {a.LastSeenUtc})
                   ON CONFLICT (tenant_id, connector_id, provider_account_id) DO UPDATE SET name = EXCLUDED.name,
                   account_type = EXCLUDED.account_type, balance = EXCLUDED.balance, currency = EXCLUDED.currency,
                   last_seen_utc = EXCLUDED.last_seen_utc", ct);
        }
    }

    public async Task<IReadOnlyList<DiscoveredProviderAccount>> ListAsync(ConnectorId? connectorId = null, CancellationToken ct = default)
    {
        var query = db.ProviderAccounts.AsNoTracking();
        if (connectorId is { } wanted)
        {
            query = query.Where(r => r.ConnectorId == wanted);
        }

        var rows = await query.OrderBy(r => r.ConnectorId).ThenBy(r => r.Name).ToListAsync(ct);
        return rows.Select(r => new DiscoveredProviderAccount(
            r.ConnectorId, r.ProviderAccountId, r.Name, r.AccountType, r.Balance, r.Currency, r.LastSeenUtc)).ToList();
    }
}
