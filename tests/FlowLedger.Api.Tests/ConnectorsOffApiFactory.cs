using Microsoft.AspNetCore.Hosting;

namespace FlowLedger.Api.Tests;

/// <summary>Host with <c>Features:ConnectorsPage=false</c>, as planned for production.</summary>
public sealed class ConnectorsOffApiFactory : FlowLedgerApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Features:ConnectorsPage", "false");
    }
}
