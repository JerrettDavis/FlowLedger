using Microsoft.AspNetCore.Hosting;

namespace FlowLedger.Api.Tests;

/// <summary>Host with <c>Api:TenantSigningKey</c> set: tenants must be vouched for by the Web app's signature.</summary>
public sealed class SigningApiFactory : FlowLedgerApiFactory
{
    public const string SigningKey = "test-signing-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Api:TenantSigningKey", SigningKey);
    }
}
