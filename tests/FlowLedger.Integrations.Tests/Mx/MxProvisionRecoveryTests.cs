using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Mx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// A previous provisioning attempt that died after CreateUser/CreateMember must be adopted, not
/// duplicated: with the user and member already present, no POST may be issued.
/// </summary>
public sealed class MxProvisionRecoveryTests : IDisposable
{
    private const string Vnd = "application/vnd.mx.api.v1+json";
    private readonly WireMockServer _server = WireMockServer.Start(new WireMockServerSettings { StartAdminInterface = false });

    [Fact]
    public async Task Existing_user_and_member_are_adopted_without_any_post()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var externalId = $"flowledger-{tenant.Value:N}";

        Stub("/users", "{\"users\":[{\"guid\":\"USR-old\",\"id\":\"" + externalId + "\"}],\"pagination\":{\"current_page\":1,\"total_pages\":1}}");
        Stub("/users/USR-old/members", """{"members":[{"guid":"MBR-old","name":"Old Bank","connection_status":"CONNECTED"}],"pagination":{"current_page":1,"total_pages":1}}""");

        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var client = new MxApiClient(http, NullLogger<MxApiClient>.Instance, new InMemoryRawPayloadArchive());
        var provider = new MxFinancialDataProvider(
            client, new MxWebhookVerifier("s"), Options.Create(new MxProviderOptions()),
            NullLogger<MxFinancialDataProvider>.Instance);

        var member = await provider.BeginConnectionAsync(tenant);

        member.ProviderId.Should().Be("USR-old|MBR-old");
        _server.LogEntries.Should().NotContain(e => e.RequestMessage!.Method == "POST",
            "adopting existing MX resources must not create duplicates");
    }

    [Fact]
    public async Task Pending_member_id_resolves_to_real_member_and_final_ids_return_null()
    {
        Stub("/users/USR-1/members", """{"members":[{"guid":"MBR-1","name":"Bank","connection_status":"CONNECTED"}],"pagination":{"current_page":1,"total_pages":1}}""");
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        var client = new MxApiClient(http, NullLogger<MxApiClient>.Instance, new InMemoryRawPayloadArchive());
        var provider = new MxFinancialDataProvider(
            client, new MxWebhookVerifier("s"), Options.Create(new MxProviderOptions()),
            NullLogger<MxFinancialDataProvider>.Instance);

        (await provider.TryResolveMemberProviderIdAsync("USR-1|PENDING")).Should().Be("USR-1|MBR-1");
        (await provider.TryResolveMemberProviderIdAsync("USR-1|MBR-1")).Should().BeNull();
    }

    private void Stub(string path, string body) =>
        _server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(body));

    public void Dispose() => _server.Dispose();
}
