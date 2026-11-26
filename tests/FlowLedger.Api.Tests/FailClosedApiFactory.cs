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

        // The route sweep fires hundreds of wrong-key requests from one address; the failed-auth throttle is covered by unit tests.
        builder.UseSetting("RateLimiting:FailedAuthPerIpPerMinute", "100000");
        builder.UseSetting("RateLimiting:GlobalPerIpPerMinute", "100000");
    }
}
