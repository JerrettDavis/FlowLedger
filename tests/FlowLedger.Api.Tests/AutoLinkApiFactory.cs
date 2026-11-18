using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Api.Tests;

/// <summary>
/// Factory variant with the opt-in identity-linking features switched on: verified-email auto-link and a configured
/// household owner (email + issuer). The default factory leaves both off, which is the secure default under test.
/// </summary>
public sealed class AutoLinkApiFactory : FlowLedgerApiFactory
{
    public const string OwnerIssuer = "https://owner-idp.test/";
    public const string OwnerEmail = "owner@household.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tenancy:AutoLinkVerifiedEmail"] = "true",
            ["Tenancy:LegacyOwnerEmail"] = OwnerEmail,
            ["Tenancy:LegacyOwnerIssuer"] = OwnerIssuer,
        }));
    }
}
