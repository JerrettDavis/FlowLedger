using FlowLedger.Application.Features.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Worker;

/// <summary>Runs a unit of work once per tenant, each in its own DI scope with that tenant active. A failing tenant never blocks the others.</summary>
internal static class TenantFanOut
{
    public static async Task ForEachTenantAsync(
        IServiceProvider root, ILogger logger, Func<IServiceProvider, Guid, Task> work, CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var listScope = root.CreateAsyncScope())
        {
            var dir = listScope.ServiceProvider.GetService<IUserDirectory>();
            // No directory registered (unit tests / bare hosts): run once under whatever tenant the scope already has.
            tenants = dir is null
                ? [listScope.ServiceProvider.GetService<ConfiguredTenantContext>()?.TenantId ?? Guid.Empty]
                : await dir.ListTenantIdsAsync(ct);
        }

        // Worker:MaxParallelTenants (default 1 = strictly serial, the historical behaviour). Raise it to sync many tenants within one
        // cron window; each tenant still has its own DI scope/DbContext. Keep it small so provider rate limits and the connection pool hold.
        var parallelism = Math.Clamp(root.GetService<IConfiguration>()?.GetValue("Worker:MaxParallelTenants", 1) ?? 1, 1, 16);

        async Task RunOne(Guid tenant)
        {
            await using var scope = root.CreateAsyncScope();
            scope.ServiceProvider.GetService<ConfiguredTenantContext>()?.Use(tenant);
            try
            {
                await work(scope.ServiceProvider, tenant);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Background work failed for tenant {TenantId}; continuing with the next tenant.", tenant);
            }
        }

        if (parallelism == 1)
        {
            foreach (var tenant in tenants)
            {
                ct.ThrowIfCancellationRequested();
                await RunOne(tenant);
            }

            return;
        }

        await Parallel.ForEachAsync(tenants, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (tenant, _) => await RunOne(tenant));
    }
}
