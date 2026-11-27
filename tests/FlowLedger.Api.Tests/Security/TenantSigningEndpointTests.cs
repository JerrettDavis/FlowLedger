using System.Net;
using FlowLedger.Api.Tenancy;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

[Collection("ApiIntegration")]
public sealed class TenantSigningEndpointTests(SigningApiFactory factory)
{
    private HttpClient Client(string? sig, Guid tenant)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {FlowLedgerApiFactory.DevApiKey}");
        c.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        if (sig is not null)
        {
            c.DefaultRequestHeaders.Add("X-Tenant-Sig", sig);
        }

        return c;
    }

    [Fact]
    public async Task Missing_or_wrong_signature_is_rejected_even_with_a_valid_api_key()
    {
        var tenant = FlowLedgerApiFactory.DemoTenantId;
        (await Client(null, tenant).GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(TenantAssertion.Sign("not-the-key", tenant, factory.ServerNow), tenant).GetAsync("/api/accounts"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // A signature minted for tenant A cannot be replayed to act as tenant B.
        (await Client(TenantAssertion.Sign(SigningApiFactory.SigningKey, tenant, factory.ServerNow), Guid.NewGuid()).GetAsync("/api/accounts"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_signature_is_accepted()
    {
        var tenant = FlowLedgerApiFactory.DemoTenantId;
        var sig = TenantAssertion.Sign(SigningApiFactory.SigningKey, tenant, factory.ServerNow);
        (await Client(sig, tenant).GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
