using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowLedger.Infrastructure.Persistence.Repositories;

/// <summary>Per-tenant account to connector links through EF (#154): the tenant query filter scopes every read and delete.</summary>
internal sealed class EfAccountConnectorLinkStore(FlowLedgerDbContext db, ITenantContext tenant) : IAccountConnectorLinkStore
{
    public async Task<IReadOnlyList<AccountConnectorLink>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.AccountConnectorLinks.AsNoTracking()
            .OrderBy(l => l.ConnectorId).ThenBy(l => l.ProviderAccountId)
            .ToListAsync(ct);
        return rows.Select(ToLink).ToList();
    }

    public async Task<AccountConnectorLink?> FindByProviderAccountAsync(ConnectorId connector, string providerAccountId, CancellationToken ct = default)
    {
        var row = await db.AccountConnectorLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ConnectorId == connector && l.ProviderAccountId == providerAccountId, ct);
        return row is null ? null : ToLink(row);
    }

    public async Task<AccountConnectorLink?> FindByAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        var row = await db.AccountConnectorLinks.AsNoTracking().FirstOrDefaultAsync(l => l.AccountId == accountId, ct);
        return row is null ? null : ToLink(row);
    }

    public async Task<bool> LinkAsync(Guid accountId, ConnectorId connector, string providerAccountId, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO account_connector_links (tenant_id, account_id, connector_id, provider_account_id)
                   VALUES ({tenantId}, {accountId}, {connector.Value}, {providerAccountId})
                   ON CONFLICT (tenant_id, account_id) DO UPDATE SET connector_id = EXCLUDED.connector_id,
                   provider_account_id = EXCLUDED.provider_account_id, linked_utc = now()", ct);
            return true;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return false; // that provider account already belongs to another account
        }
    }

    public Task PinManualAsync(Guid accountId, CancellationToken ct = default)
        => LinkAsync(accountId, AccountConnectorLinks.Manual, accountId.ToString("N"), ct);

    public async Task<bool> UnlinkAsync(Guid accountId, CancellationToken ct = default)
        => await db.AccountConnectorLinks.Where(l => l.AccountId == accountId).ExecuteDeleteAsync(ct) > 0;

    private static bool IsUniqueViolation(Exception ex)
        => ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
           || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static AccountConnectorLink ToLink(AccountConnectorLinkRecord r)
        => new(r.AccountId, r.ConnectorId, r.ProviderAccountId, r.LinkedUtc);
}
