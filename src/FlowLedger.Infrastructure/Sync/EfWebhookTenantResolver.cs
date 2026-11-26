using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Sync;

internal sealed class EfWebhookTenantResolver(DbContextOptions<FlowLedgerDbContext> options) : IWebhookTenantResolver
{
    public async Task<Guid?> ResolveAsync(string providerName, string memberProviderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(memberProviderId))
        {
            return null;
        }

        // Explicitly unscoped: the whole point is to find the tenant from the member id.
        await using var db = new FlowLedgerDbContext(options, new NoOpDomainEventDispatcher(), UnscopedTenantContext.Instance);
        var owners = await db.ProviderConnections.AsNoTracking()
            .Where(c => c.ProviderName == providerName && c.MemberProviderId == memberProviderId)
            .Select(c => c.TenantId)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);

        // Provider ids are unique per connection; if two tenants claim one, refuse rather than guess.
        return owners.Count == 1 ? owners[0] : null;
    }
}
