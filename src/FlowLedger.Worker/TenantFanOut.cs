using FlowLedger.Application.Features.Users;
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

        foreach (var tenant in tenants)
        {
            ct.ThrowIfCancellationRequested();
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
    }
}
