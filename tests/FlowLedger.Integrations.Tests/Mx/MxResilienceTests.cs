using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Resilience and error-taxonomy tests for the MX client/provider against WireMock fault stubs:
/// transient 503-then-200 retry, 429 → RateLimited with RetryAfter, 401 → Fatal, and
/// CHALLENGED member state → NeedsUserAction. No real API key.
/// </summary>
public sealed class MxResilienceTests : IDisposable
{
    private const string Vnd = "application/vnd.mx.api.v1+json";
    private const string UserGuid = "USR-r";
    private const string MemberGuid = "MBR-r";
    private const string AccountGuid = "ACT-r";

    private readonly WireMockServer _server;
    private readonly ServiceProvider _provider;

    // IFinancialDataProvider is registered as Scoped. Keep one long-lived scope for the tests
    // so the provider outlives each call. The scope (and provider within it) is disposed together
    // with this test class.
    private readonly IServiceScope _scope;

    public MxResilienceTests()
    {
        _server = WireMockServer.Start(new WireMockServerSettings { StartAdminInterface = false });

        // Build a real DI container via AddMxProvider so the typed MxApiClient carries the
        // standard resilience handler (retries on transient 5xx). Credentials point at WireMock.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mx:Enabled"] = "true",
                ["Mx:ApiKey"] = "test-key",
                ["Mx:ClientId"] = "test-client",
                ["Mx:BaseUrl"] = _server.Url,
                ["Mx:WebhookSecret"] = "test-secret",
                ["Mx:Provider:RecordsPerPage"] = "25",
                ["Mx:Provider:DefaultInstitutionCode"] = "wm",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMxProvider(config);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _server.Dispose();
    }

    /// <summary>The DI-configured client — carries the standard resilience handler (retries 5xx).</summary>
    private MxApiClient CreateResilientClient() => _provider.GetRequiredService<MxApiClient>();

    /// <summary>
    /// A bare client WITHOUT the resilience handler. Used for error-mapping assertions (429/401)
    /// so the standard handler's retry/timeout policy does not interfere with the single-response
    /// status-to-exception mapping under test.
    /// </summary>
    private MxApiClient CreateBareClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!, UriKind.Absolute) };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("c:k"));
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(Vnd));
        return new MxApiClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<MxApiClient>.Instance);
    }

    private IFinancialDataProvider CreateProvider() =>
        _scope.ServiceProvider.GetRequiredService<IFinancialDataProvider>();

    // ── Transient retry: 503 then 200 ────────────────────────────────────────────

    [Fact]
    public async Task Transient_503_then_200_is_retried_and_succeeds()
    {
        const string scenario = "retry";
        var path = $"/users/{UserGuid}/members/{MemberGuid}/status";

        _server
            .Given(Request.Create().WithPath(path).UsingGet())
            .InScenario(scenario)
            .WillSetStateTo("after-503")
            .RespondWith(Response.Create().WithStatusCode(503));

        _server
            .Given(Request.Create().WithPath(path).UsingGet())
            .InScenario(scenario)
            .WhenStateIs("after-503")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(MemberStatusBody("CONNECTED")));

        var client = CreateResilientClient();
        var member = await client.GetMemberStatusAsync(UserGuid, MemberGuid, CancellationToken.None);

        member.ConnectionStatus.Should().Be("CONNECTED");
        // The standard resilience handler should have retried at least once.
        _server.LogEntries.Count(e => e.RequestMessage?.Path == path).Should().BeGreaterThanOrEqualTo(2);
    }

    // ── 429 → RateLimited with RetryAfter ────────────────────────────────────────

    [Fact]
    public async Task RateLimited_429_throws_with_retry_after()
    {
        var path = $"/users/{UserGuid}/members/{MemberGuid}/status";

        _server
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(429)
                .WithHeader("Retry-After", "42"));

        var client = CreateBareClient();

        var act = async () => await client.GetMemberStatusAsync(UserGuid, MemberGuid, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<RateLimitedProviderException>()).Which;
        ex.RetryAfter.Should().NotBeNull();
        ex.RetryAfter!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(30));
    }

    // ── 401 → Fatal ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthorized_401_throws_fatal()
    {
        var path = $"/users/{UserGuid}/members/{MemberGuid}/status";

        _server
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        var client = CreateBareClient();

        var act = async () => await client.GetMemberStatusAsync(UserGuid, MemberGuid, CancellationToken.None);

        await act.Should().ThrowAsync<FatalProviderException>();
    }

    // ── CHALLENGED member → NeedsUserAction ──────────────────────────────────────

    [Fact]
    public async Task Challenged_member_throws_needs_user_action_on_get_accounts()
    {
        var statusPath = $"/users/{UserGuid}/members/{MemberGuid}/status";

        _server
            .Given(Request.Create().WithPath(statusPath).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", Vnd)
                .WithBody(MemberStatusBody("CHALLENGED")));

        var provider = CreateProvider();
        var composite = $"{UserGuid}|{MemberGuid}";

        var act = async () => await provider.GetAccountsAsync(composite, CancellationToken.None);

        await act.Should().ThrowAsync<NeedsUserActionProviderException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string MemberStatusBody(string status) =>
        "{\"member\":{\"guid\":\"" + MemberGuid + "\",\"name\":\"Bank\",\"connection_status\":\"" +
        status + "\",\"connection_status_message\":\"msg\",\"user_guid\":\"" + UserGuid + "\"}}";
}
