using Microsoft.AspNetCore.Hosting;

namespace FlowLedger.Api.Tests;

/// <summary>Development host with <c>Tenancy:RequireTenantHeader=true</c>: must fail closed like Production.</summary>
public sealed class RequireTenantHeaderApiFactory : FlowLedgerApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Tenancy:RequireTenantHeader", "true");
    }
}
