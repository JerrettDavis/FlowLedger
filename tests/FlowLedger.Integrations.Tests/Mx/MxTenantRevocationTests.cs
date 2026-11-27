using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>#189: erasing a tenant asks MX to delete the user created for it (and nothing else).</summary>
public sealed class MxTenantRevocationTests : IDisposable
{
    private const string Vnd = "application/vnd.mx.api.v1+json";
    private readonly WireMockServer _server = WireMockServer.Start(new WireMockServerSettings { StartAdminInterface = false });

    private MxFinancialDataProvider Provider() => new(
        new MxApiClient(new HttpClient { BaseAddress = new Uri(_server.Url!) }, NullLogger<MxApiClient>.Instance, new InMemoryRawPayloadArchive()),
        new MxWebhookVerifier("s"), Options.Create(new MxProviderOptions()), NullLogger<MxFinancialDataProvider>.Instance);

    private void StubUsers(string body) =>
        _server.Given(Request.Create().WithPath("/users").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(body));

    [Fact]
    public async Task The_users_MX_account_is_deleted_by_its_external_id()
    {
        var tenant = TenantId.From(Guid.NewGuid());
        var other = TenantId.From(Guid.NewGuid());
        StubUsers("{\"users\":[{\"guid\":\"USR-other\",\"id\":\"flowledger-" + other.Value.ToString("N") + "\"},{\"guid\":\"USR-mine\",\"id\":\"flowledger-" + tenant.Value.ToString("N") + "\"}],\"pagination\":{\"current_page\":1,\"total_pages\":1}}");
        _server.Given(Request.Create().WithPath("/users/USR-mine").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));

        var outcome = await ((IProviderTenantRevoker)Provider()).RevokeTenantAsync(tenant);

        outcome.Should().Be(ProviderRevocationOutcome.Revoked);
        var deletes = _server.LogEntries.Where(e => e.RequestMessage!.Method == "DELETE").Select(e => e.RequestMessage!.Path).ToList();
        deletes.Should().Equal("/users/USR-mine");
    }

    [Fact]
    public async Task A_tenant_that_never_connected_has_nothing_to_revoke_and_nothing_is_deleted()
    {
        StubUsers("{\"users\":[],\"pagination\":{\"current_page\":1,\"total_pages\":1}}");

        var outcome = await ((IProviderTenantRevoker)Provider()).RevokeTenantAsync(TenantId.From(Guid.NewGuid()));

        outcome.Should().Be(ProviderRevocationOutcome.NothingToRevoke);
        _server.LogEntries.Should().NotContain(e => e.RequestMessage!.Method == "DELETE");
    }

    public void Dispose() => _server.Dispose();
}
