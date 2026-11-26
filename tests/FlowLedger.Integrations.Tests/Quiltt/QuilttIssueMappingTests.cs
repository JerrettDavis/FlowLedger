using System.Net;
using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Quiltt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Quiltt;

/// <summary>
/// Quiltt-native failure shapes (verified against the Quiltt sandbox, see docs/quiltt-sandbox-scenarios.md) mapped onto the
/// connector-neutral <see cref="ProviderIssue"/> vocabulary. This is the reference mapping other connectors copy.
/// </summary>
public sealed class QuilttIssueMappingTests
{
    // ── Table: ConnectionStatus → kind / retry / user action / status ──

    // OBSERVED live in the sandbox: SYNCED, ERROR_REPAIRABLE (connectionSimulateError), DISCONNECTING -> DISCONNECTED (connectionDisconnect).
    // SCHEMA-DERIVED only (enum + descriptions; no sandbox login produced them): ERROR_INSTITUTION, ERROR_PROVIDER, ERROR_SERVICE,
    // SYNCING, INITIALIZING, UPGRADING.
    public static TheoryData<string, ProviderIssueKind, bool, bool, ConnectionStatus> StatusTable => new()
    {
        { "SYNCED", ProviderIssueKind.None, false, false, ConnectionStatus.Healthy },
        { "SYNCING", ProviderIssueKind.None, false, false, ConnectionStatus.Syncing },
        { "INITIALIZING", ProviderIssueKind.None, false, false, ConnectionStatus.Syncing },
        { "UPGRADING", ProviderIssueKind.None, false, false, ConnectionStatus.Syncing },
        { "ERROR_REPAIRABLE", ProviderIssueKind.ReauthRequired, false, true, ConnectionStatus.NeedsUserAction },
        { "ERROR_INSTITUTION", ProviderIssueKind.InstitutionUnavailable, true, false, ConnectionStatus.Degraded },
        { "ERROR_PROVIDER", ProviderIssueKind.ProviderUnavailable, true, false, ConnectionStatus.Degraded },
        { "ERROR_SERVICE", ProviderIssueKind.ServiceUnavailable, true, false, ConnectionStatus.Degraded },
        { "DISCONNECTED", ProviderIssueKind.Disconnected, false, true, ConnectionStatus.NeedsUserAction },
        { "DISCONNECTING", ProviderIssueKind.Disconnected, false, true, ConnectionStatus.NeedsUserAction },
        { "SOMETHING_NEW", ProviderIssueKind.Unknown, false, false, ConnectionStatus.Error },
    };

    [Theory]
    [MemberData(nameof(StatusTable))]
    public void ConnectionStatus_MapsToKind(string status, ProviderIssueKind kind, bool retryable, bool userAction, ConnectionStatus lifecycle)
    {
        var health = QuilttIssueMapper.Aggregate([("conn_1", status)]);

        health.Status.Should().Be(lifecycle);
        health.Issue!.Kind.Should().Be(kind);
        health.Issue.Retryable.Should().Be(retryable);
        health.Issue.RequiresUserAction.Should().Be(userAction);
        if (kind != ProviderIssueKind.None)
        {
            health.Issue.ProviderCode.Should().Be($"{status.ToUpperInvariant()}:conn_1");
            QuilttProviderCode.TryGetConnectionId(health.Issue.ProviderCode).Should().Be("conn_1");
        }
    }

    [Fact]
    public void NoConnections_IsPending() =>
        QuilttIssueMapper.Aggregate([]).Status.Should().Be(ConnectionStatus.ConnectionPending);

    [Fact]
    public void Aggregate_WorstIssueWins_UserActionBeforeOutages()
    {
        var health = QuilttIssueMapper.Aggregate([("c1", "ERROR_SERVICE"), ("c2", "ERROR_REPAIRABLE"), ("c3", "SYNCED")]);

        health.Issue!.Kind.Should().Be(ProviderIssueKind.ReauthRequired);
        QuilttProviderCode.TryGetConnectionId(health.Issue.ProviderCode).Should().Be("c2");
    }

    [Fact]
    public void Aggregate_OldDisconnectedConnection_DoesNotFlagARelinkedProfile()
    {
        QuilttIssueMapper.Aggregate([("old", "DISCONNECTED"), ("new", "SYNCED")]).IsHealthy.Should().BeTrue();
        QuilttIssueMapper.Aggregate([("old", "DISCONNECTED")]).Issue!.Kind.Should().Be(ProviderIssueKind.Disconnected);
    }

    [Fact]
    public void Aggregate_Syncing_WhenNothingIsWrong() =>
        QuilttIssueMapper.Aggregate([("a", "SYNCED"), ("b", "SYNCING")]).Status.Should().Be(ConnectionStatus.Syncing);

    [Theory]
    [InlineData(null, null)]
    [InlineData("ERROR_REPAIRABLE", null)]
    [InlineData("ERROR_REPAIRABLE:conn_9", "conn_9")]
    public void ProviderCode_RoundTrips(string? code, string? id) =>
        QuilttProviderCode.TryGetConnectionId(code).Should().Be(id);

    // ── Table: HTTP failures (shapes captured from the live API) ──

    public static TheoryData<HttpStatusCode, bool, ProviderIssueKind, bool, bool> HttpTable => new()
    {
        // 401 {"message":"Not Authenticated","instruction":"Are you sending a valid API Key secret ..."}: operator config.
        { HttpStatusCode.Unauthorized, true, ProviderIssueKind.Internal, true, false },
        // 401 on GraphQL with a bad/expired session token: a fresh sync mints a new one.
        { HttpStatusCode.Unauthorized, false, ProviderIssueKind.Internal, true, false },
        // Documented: calls on an ERROR_REPAIRABLE connection get 403.
        { HttpStatusCode.Forbidden, false, ProviderIssueKind.ReauthRequired, false, true },
        // 429 {"message":"Too Many Requests","instruction":"You have exceeded the number of Sessions you can create per hour..."}
        { HttpStatusCode.TooManyRequests, true, ProviderIssueKind.RateLimited, true, false },
        { HttpStatusCode.InternalServerError, false, ProviderIssueKind.Internal, true, false },
        { HttpStatusCode.BadGateway, false, ProviderIssueKind.ServiceUnavailable, true, false },
        { HttpStatusCode.ServiceUnavailable, true, ProviderIssueKind.ServiceUnavailable, true, false },
        { HttpStatusCode.GatewayTimeout, false, ProviderIssueKind.ServiceUnavailable, true, false },
        { HttpStatusCode.BadRequest, false, ProviderIssueKind.Internal, true, false },
    };

    [Theory]
    [MemberData(nameof(HttpTable))]
    public void HttpFailure_MapsToKind(HttpStatusCode status, bool isSession, ProviderIssueKind kind, bool retryable, bool userAction)
    {
        var ex = QuilttIssueMapper.FromHttpFailure(status, isSession ? QuilttIssueMapper.Endpoint.Session : QuilttIssueMapper.Endpoint.GraphQl, null, TimeSpan.FromMinutes(15));
        var issue = ProviderIssue.FromException(ex);

        issue.Kind.Should().Be(kind);
        issue.Retryable.Should().Be(retryable);
        issue.RequiresUserAction.Should().Be(userAction);
        issue.Message.Should().NotContain("Bearer").And.NotContain("Not Authenticated");
    }

    [Fact]
    public void RateLimit_WithoutRetryAfter_UsesConfiguredFallback()
    {
        var ex = (RateLimitedProviderException)QuilttIssueMapper.FromHttpFailure(
            HttpStatusCode.TooManyRequests, QuilttIssueMapper.Endpoint.Session, null, TimeSpan.FromMinutes(15));

        ex.RetryAfter.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RateLimit_HonoursRetryAfterHeader()
    {
        var ex = (RateLimitedProviderException)QuilttIssueMapper.FromHttpFailure(
            HttpStatusCode.TooManyRequests, QuilttIssueMapper.Endpoint.GraphQl, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));

        ex.RetryAfter.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(30), TimeSpan.FromSeconds(5));
    }

    // ── GraphQL error[] shapes ──

    private static QGraphQlError Err(string message, string? code = null) => new()
    {
        Message = message,
        Extensions = code is null ? null : new() { ["code"] = System.Text.Json.JsonSerializer.SerializeToElement(code) },
    };

    [Theory]
    // Real: query validation failures from the live API when our query was wrong.
    [InlineData("Field 'nodes' doesn't exist on type 'Account'", "undefinedField", ProviderIssueKind.Internal)]
    [InlineData("Field 'accounts' doesn't accept argument 'first'", "argumentNotAccepted", ProviderIssueKind.Internal)]
    [InlineData("Not Authenticated", null, ProviderIssueKind.Internal)]
    // Real: connectionSimulateError with an unknown (or DISCONNECTED) connection id.
    [InlineData("No Connection found for `id: \"conn_x\"`", "NOT_FOUND", ProviderIssueKind.Internal)]
    [InlineData("Forbidden", "FORBIDDEN", ProviderIssueKind.ReauthRequired)]
    [InlineData("Too many requests", "RATE_LIMITED", ProviderIssueKind.RateLimited)]
    [InlineData("Upstream timeout", "TIMEOUT", ProviderIssueKind.ServiceUnavailable)]
    public void GraphQlErrors_MapToKind(string message, string? code, ProviderIssueKind kind)
    {
        var ex = QuilttIssueMapper.FromGraphQlErrors([Err(message, code)], TimeSpan.FromMinutes(15));

        ProviderIssue.FromException(ex).Kind.Should().Be(kind);
        ex.Message.Should().NotContain(message.Contains("Field") ? "Field" : "\u0000"); // raw error text is not echoed
    }

    // ── Provider end-to-end over stubbed wire (every scenario's profile state) ──

    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static QuilttFinancialDataProvider Provider(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var opts = Options.Create(new QuilttOptions { ApiKeySecret = "key" });
        return new QuilttFinancialDataProvider(
            new QuilttApiClient(new HttpClient(new Stub(respond)), opts), opts, null, NullLogger<QuilttFinancialDataProvider>.Instance);
    }

    private static string Connections(params (string Id, string Status)[] c) =>
        "{\"data\":{\"connections\":[" +
        string.Join(',', c.Select(x => $"{{\"id\":\"{x.Id}\",\"status\":\"{x.Status}\",\"at\":\"2026-11-26T00:05:06Z\",\"provider\":\"MOCK\"}}")) + "]}}";

    private static string Accounts(string connId, string status) =>
        "{\"data\":{\"accounts\":[{\"id\":\"acct_1\",\"name\":\"Spending\",\"kind\":\"DEPOSITORY\",\"state\":\"OPEN\",\"mask\":\"5944\",\"currencyCode\":\"USD\"," +
        "\"balance\":{\"current\":548.56,\"available\":null},\"institution\":{\"name\":\"Mock Sandbox Bank\"}," +
        $"\"connection\":{{\"id\":\"{connId}\",\"status\":\"{status}\"}}}}]}}}}";

    private static Func<HttpRequestMessage, string, HttpResponseMessage> Wire(string connectionsJson, string accountsJson) => (req, body) =>
        req.RequestUri!.Host.StartsWith("auth", StringComparison.Ordinal) ? Json("""{"token":"tok","userId":"p_1"}""")
        : body.Contains("FlowLedgerConnections") ? Json(connectionsJson)
        : body.Contains("FlowLedgerAccounts") ? Json(accountsJson)
        : Json("""{"data":{"transactions":{"nodes":[],"pageInfo":{"hasNextPage":false}}}}""");

    [Theory]
    [InlineData("ERROR_REPAIRABLE", ProviderIssueKind.ReauthRequired, typeof(NeedsUserActionProviderException))]
    [InlineData("ERROR_INSTITUTION", ProviderIssueKind.InstitutionUnavailable, typeof(TransientProviderException))]
    [InlineData("ERROR_PROVIDER", ProviderIssueKind.ProviderUnavailable, typeof(TransientProviderException))]
    [InlineData("ERROR_SERVICE", ProviderIssueKind.ServiceUnavailable, typeof(TransientProviderException))]
    [InlineData("DISCONNECTED", ProviderIssueKind.Disconnected, typeof(NeedsUserActionProviderException))]
    public async Task BrokenOnlyConnection_HealthAndReadsAgree(string status, ProviderIssueKind kind, Type exceptionType)
    {
        var p = Provider(Wire(Connections(("conn_9", status)), Accounts("conn_9", status)));

        var health = await p.GetConnectionHealthAsync("p_1");
        health.Issue!.Kind.Should().Be(kind);
        QuilttProviderCode.TryGetConnectionId(health.Issue.ProviderCode).Should().Be("conn_9");

        var thrown = (await p.Invoking(x => x.GetAccountsAsync("p_1")).Should().ThrowAsync<ProviderException>()).Which;
        thrown.Should().BeOfType(exceptionType);
        thrown.Kind.Should().Be(kind);
    }

    [Fact]
    public async Task HealthyConnectionPlusBrokenOne_StillSyncsAndReportsTheBrokenOne()
    {
        var accounts = "{\"data\":{\"accounts\":[" +
            "{\"id\":\"a1\",\"name\":\"Spending\",\"kind\":\"DEPOSITORY\",\"state\":\"OPEN\",\"currencyCode\":\"USD\",\"connection\":{\"id\":\"ok\",\"status\":\"SYNCED\"}}," +
            "{\"id\":\"a2\",\"name\":\"Old\",\"kind\":\"DEPOSITORY\",\"state\":\"OPEN\",\"currencyCode\":\"USD\",\"connection\":{\"id\":\"bad\",\"status\":\"ERROR_REPAIRABLE\"}}]}}";
        var p = Provider(Wire(Connections(("ok", "SYNCED"), ("bad", "ERROR_REPAIRABLE")), accounts));

        (await p.GetAccountsAsync("p_1")).Should().HaveCount(2);
        var health = await p.GetConnectionHealthAsync("p_1");
        health.Status.Should().Be(ConnectionStatus.NeedsUserAction);
        QuilttProviderCode.TryGetConnectionId(health.Issue!.ProviderCode).Should().Be("bad");
    }

    [Fact]
    public async Task CapturedSandbox_SimulatedErrorRepairable_AccountsStayReadable_ButSoleConnectionBlocksSync()
    {
        // Real payload after connectionSimulateError: status flips, accounts/balances remain (stale) and readable.
        var p = Provider(Wire(
            """{"data":{"connections":[{"id":"conn_1353zGOwESRQn8cFUccy6k","status":"ERROR_REPAIRABLE","at":"2026-11-26T00:05:06Z","provider":"MOCK"}]}}""",
            """{"data":{"accounts":[{"id":"acct_1353zGPa6hCIaOTRSyhWgj","name":"Spending","kind":"DEPOSITORY","state":"OPEN","mask":"5944","currencyCode":"USD","balance":{"current":548.56,"available":null},"institution":{"name":"Mock Sandbox Bank"},"connection":{"id":"conn_1353zGOwESRQn8cFUccy6k","status":"ERROR_REPAIRABLE"}}]}}"""));

        var thrown = (await p.Invoking(x => x.GetAccountsAsync("p_1")).Should().ThrowAsync<NeedsUserActionProviderException>()).Which;
        thrown.Kind.Should().Be(ProviderIssueKind.ReauthRequired);
        QuilttProviderCode.TryGetConnectionId(thrown.ProviderCode).Should().Be("conn_1353zGOwESRQn8cFUccy6k");
    }

    [Fact]
    public async Task SyncedProfile_IsHealthy_AndGetConnectionStatusAgrees()
    {
        var p = Provider(Wire(Connections(("c", "SYNCED")), Accounts("c", "SYNCED")));

        (await p.GetConnectionHealthAsync("p_1")).IsHealthy.Should().BeTrue();
        (await p.GetConnectionStatusAsync("p_1")).Should().Be(ConnectionStatus.Healthy);
    }

    [Fact]
    public async Task SessionQuota429_IsRateLimited_WithFallbackRetryAfter()
    {
        // Real body from the sandbox after minting too many sessions for one profile (no Retry-After header).
        var p = Provider((_, _) => Json(
            """{"message":"Too Many Requests","instruction":"You have exceeded the number of Sessions you can create per hour for this Profile. Please try again later.","requestId":"x"}""",
            HttpStatusCode.TooManyRequests));

        var ex = (await p.Invoking(x => x.GetConnectionHealthAsync("p_1")).Should().ThrowAsync<RateLimitedProviderException>()).Which;
        ex.Kind.Should().Be(ProviderIssueKind.RateLimited);
        ex.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task BadApiKey401_IsInternalNotUserAction()
    {
        var p = Provider((_, _) => Json(
            """{"message":"Not Authenticated","instruction":"Are you sending a valid API Key secret in the `Authorization` header?","requestId":"x"}""",
            HttpStatusCode.Unauthorized));

        var ex = (await p.Invoking(x => x.GetConnectionHealthAsync("p_1")).Should().ThrowAsync<ProviderException>()).Which;
        ProviderIssue.FromException(ex).Should().Match<ProviderIssue>(i => i.Kind == ProviderIssueKind.Internal && !i.RequiresUserAction);
    }

    [Fact]
    public async Task Connector_Down_TransportFailure_IsServiceUnavailable()
    {
        var p = Provider((_, _) => throw new HttpRequestException("dns"));

        var ex = (await p.Invoking(x => x.GetConnectionHealthAsync("p_1")).Should().ThrowAsync<TransientProviderException>()).Which;
        ex.Kind.Should().Be(ProviderIssueKind.ServiceUnavailable);
    }
}

public sealed class QuilttSessionCacheTests
{
    private static QuilttSession Session(string profile, DateTimeOffset? expires) => new("tok-" + profile, profile, expires);

    [Fact]
    public void ReusesASessionUntilShortlyBeforeItExpires()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-25T12:00:00Z"));
        var cache = new QuilttSessionCache(clock);
        cache.Put(Session("p_1", clock.GetUtcNow().AddHours(24)));

        cache.TryGet("p_1").Should().NotBeNull();
        clock.Advance(TimeSpan.FromMinutes(59));
        cache.TryGet("p_1").Should().NotBeNull();
        clock.Advance(TimeSpan.FromMinutes(2)); // past the 1h max age
        cache.TryGet("p_1").Should().BeNull();
    }

    [Fact]
    public void NearExpiryOrUnknownExpiry_IsNeverServed()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-25T12:00:00Z"));
        var cache = new QuilttSessionCache(clock);

        cache.Put(Session("soon", clock.GetUtcNow().AddMinutes(5)));
        cache.Put(Session("unknown", null));

        cache.TryGet("soon").Should().BeNull();
        cache.TryGet("unknown").Should().BeNull();
    }

    [Fact]
    public async Task ApiClient_MintsOncePerProfile_AcrossManyCalls()
    {
        var mints = 0;
        var handler = new CountingHandler(() => { mints++; return """{"token":"t","userId":"p_1","expiresAt":"2999-01-01T00:00:00Z"}"""; });
        var client = new QuilttApiClient(new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new QuilttOptions { ApiKeySecret = "k" }), new QuilttSessionCache());

        for (var i = 0; i < 5; i++)
        {
            await client.CreateSessionAsync("p_1", default);
        }

        mints.Should().Be(1);
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingHandler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Created)
            {
                Content = new StringContent(body(), System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
