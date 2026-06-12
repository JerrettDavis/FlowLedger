using Microsoft.AspNetCore.Hosting;

namespace FlowLedger.Api.Tests;

/// <summary>
/// Factory variant that runs the app in the Production environment to exercise
/// fail-closed <c>HeaderTenantContext</c> behavior. The base factory supplies a
/// non-default Api:Key, so the Production startup guard passes; the tenant context
/// still fails closed (401) when no X-Tenant-Id header is present.
/// </summary>
public sealed class FailClosedApiFactory : FlowLedgerApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}
