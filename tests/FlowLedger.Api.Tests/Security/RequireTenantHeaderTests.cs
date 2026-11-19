using System.Net;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

[Collection("ApiIntegration")]
public sealed class RequireTenantHeaderTests(RequireTenantHeaderApiFactory factory)
{
    [Fact]
    public async Task Development_with_flag_rejects_requests_without_tenant_header()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {FlowLedgerApiFactory.DevApiKey}");

        var response = await client.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Development_with_flag_accepts_requests_with_tenant_header()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {FlowLedgerApiFactory.DevApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", FlowLedgerApiFactory.DemoTenantId.ToString());

        var response = await client.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
