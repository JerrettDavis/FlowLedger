using System.Net;
using System.Security.Cryptography;
using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Quiltt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Quiltt;

public sealed class QuilttProviderTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static QuilttFinancialDataProvider Make(Stub stub, string? webhookSecret = "whsec", QuilttOptions? opts = null)
    {
        opts ??= new QuilttOptions { ApiKeySecret = "key", ConnectorId = "con_1", WebhookSecret = webhookSecret };
        opts.ApiKeySecret ??= "key";
        var o = Options.Create(opts);
        var verifier = webhookSecret is null ? null : new QuilttWebhookVerifier(webhookSecret, opts.WebhookTolerance);
        return new QuilttFinancialDataProvider(
            new QuilttApiClient(new HttpClient(stub), o), o, verifier, NullLogger<QuilttFinancialDataProvider>.Instance);
    }

    private const string AccountsJson = """
        {"data":{"accounts":[
          {"id":"acc_1","name":"Everyday","kind":"DEPOSITORY","state":"OPEN","mask":"1234","currencyCode":"USD","balance":{"current":100.5,"available":90},"institution":{"name":"Test Bank"},"connection":{"id":"conn_1","status":"SYNCED"}},
          {"id":"acc_2","name":"Visa","kind":"CREDIT","state":"OPEN","currencyCode":"USD","balance":{"current":-250.25},"institution":{"name":"Test Bank"},"connection":{"id":"conn_1","status":"SYNCED"}}
        ]}}
        """;

    private const string TransactionsJson = """
        {"data":{"transactions":{"nodes":[
          {"id":"t1","date":"2026-11-01","description":" COFFEE ","amount":-4.5,"currencyCode":"USD","status":"POSTED","entryType":"DEBIT","account":{"id":"acc_1"}},
          {"id":"t2","date":"2026-11-02","description":"PAYCHECK","amount":1000,"status":"PENDING","entryType":"CREDIT","account":{"id":"acc_1"}},
          {"id":"t3","date":"2026-11-03","description":"STORE","amount":30,"status":"POSTED","entryType":"DEBIT","account":{"id":"acc_2"}}
        ],"pageInfo":{"hasNextPage":false}}}}
        """;

    private static Stub HappyPath() => new((req, body) =>
        req.RequestUri!.Host.StartsWith("auth") ? Json("""{"token":"tok","userId":"p_1"}""")
        : body.Contains("FlowLedgerAccounts") ? Json(AccountsJson)
        : Json(TransactionsJson));

    [Fact]
    public async Task BeginConnection_CreatesProfile_WithBearerKey()
    {
        var stub = HappyPath();
        var p = Make(stub);

        var member = await p.BeginConnectionAsync(FlowLedger.Domain.ValueObjects.TenantId.From(Guid.NewGuid()));

        member.ProviderId.Should().Be("p_1");
        member.Status.Should().Be(ConnectionStatus.ConnectionPending);
        stub.Calls.Should().ContainSingle().Which.Url.Should().EndWith("users/sessions");
    }

    [Fact]
    public async Task CreateSession_ForExistingProfile_SendsUserId()
    {
        var stub = HappyPath();
        var session = await Make(stub).CreateSessionAsync("p_9");

        session.Token.Should().Be("tok");
        stub.Calls[0].Body.Should().Contain("p_9");
    }

    [Fact]
    public async Task Accounts_MapTypesAndLiabilitySign()
    {
        var accounts = await Make(HappyPath()).GetAccountsAsync("p_1");

        accounts.Should().HaveCount(2);
        accounts[0].AccountType.Should().Be("CHECKING");
        accounts[0].Name.Should().Be("Test Bank - Everyday ••1234");
        accounts[0].Balance.Amount.Should().Be(100.5m);
        accounts[1].AccountType.Should().Be("CREDIT_CARD");
        accounts[1].Balance.Amount.Should().Be(250.25m); // owed is positive
    }

    [Fact]
    public async Task Transactions_FilterByAccount_SignAndPending_AndCursorIgnoresPending()
    {
        var stub = HappyPath();
        var p = Make(stub);
        await p.GetAccountsAsync("p_1");

        var page = await p.GetTransactionsAsync("acc_1", SyncCursor.Initial);

        page.Items.Should().HaveCount(2);
        page.Items[0].Amount.Amount.Should().Be(-4.5m);
        page.Items[0].RawDescription.Should().Be("COFFEE");
        page.Items[1].IsPending.Should().BeTrue();
        page.NextCursor.Value.Should().Be("2026-11-01");

        // The second account reuses the same read: one auth + one accounts + one transactions request in total.
        var other = await p.GetTransactionsAsync("acc_2", SyncCursor.Initial);
        other.Items.Should().ContainSingle().Which.Amount.Amount.Should().Be(-30m);
        stub.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Unauthorized_SurfacesNeedsUserAction()
    {
        var p = Make(new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await p.Invoking(x => x.GetAccountsAsync("p_1")).Should().ThrowAsync<NeedsUserActionProviderException>();
    }

    [Fact]
    public async Task ServerError_IsTransient()
    {
        var p = Make(new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        await p.Invoking(x => x.GetAccountsAsync("p_1")).Should().ThrowAsync<TransientProviderException>();
    }

    [Fact]
    public void AccountKinds_MapToFlowLedgerTypes()
    {
        QuilttMapper.MapType("DEPOSITORY", "Everyday Checking").Should().Be("CHECKING");
        QuilttMapper.MapType("DEPOSITORY", "High Yield Savings").Should().Be("SAVINGS");
        QuilttMapper.MapType("CREDIT", "Visa").Should().Be("CREDIT_CARD");
        QuilttMapper.MapType("LOAN", "Auto").Should().Be("LOAN");
        QuilttMapper.MapType("LOAN", "Home Mortgage").Should().Be("MORTGAGE");
        QuilttMapper.MapType("INVESTMENT", "IRA").Should().Be("INVESTMENT");
    }

    [Fact]
    public async Task Transactions_FollowEndCursor()
    {
        var calls = 0;
        var stub = new Stub((req, body) =>
        {
            if (req.RequestUri!.Host.StartsWith("auth")) { return Json("""{"token":"tok","userId":"p_1"}"""); }
            if (body.Contains("FlowLedgerAccounts")) { return Json(AccountsJson); }
            calls++;
            return Json(body.Contains("\"after\":null")
                ? """{"data":{"transactions":{"nodes":[{"id":"t1","date":"2026-11-01","description":"A","amount":-1,"entryType":"DEBIT","status":"POSTED","account":{"id":"acc_1"}}],"pageInfo":{"hasNextPage":true,"endCursor":"c1"}}}}"""
                : """{"data":{"transactions":{"nodes":[{"id":"t2","date":"2026-11-02","description":"B","amount":-2,"entryType":"DEBIT","status":"POSTED","account":{"id":"acc_1"}}],"pageInfo":{"hasNextPage":false}}}}""");
        });
        var p = Make(stub);
        await p.GetAccountsAsync("p_1");

        var page = await p.GetTransactionsAsync("acc_1", SyncCursor.Initial);

        page.Items.Select(i => i.ProviderId).Should().Equal("t1", "t2");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ConnectionStatus_UsesQuilttStatusEnum()
    {
        Task<ConnectionStatus> Status(string s) => Make(new Stub((req, _) =>
            req.RequestUri!.Host.StartsWith("auth") ? Json("""{"token":"tok","userId":"p_1"}""")
            : Json("""{"data":{"accounts":[{"id":"a","name":"A","kind":"DEPOSITORY","connection":{"id":"c","status":"%S%"}}]}}""".Replace("%S%", s))))
            .GetConnectionStatusAsync("p_1");

        (await Status("SYNCED")).Should().Be(ConnectionStatus.Healthy);
        (await Status("SYNCING")).Should().Be(ConnectionStatus.Syncing);
        (await Status("ERROR_REPAIRABLE")).Should().Be(ConnectionStatus.NeedsUserAction);
        (await Status("ERROR_INSTITUTION")).Should().Be(ConnectionStatus.Degraded);
    }

    [Theory]
    [InlineData("connection.synced.successful", true)]
    [InlineData("connection.synced.successful.initial", true)]
    [InlineData("connection.synced.successful.historical", true)]
    [InlineData("connection.created", true)]
    [InlineData("account.verified", true)]
    [InlineData("connection.synced.errored.repairable", false)]
    [InlineData("profile.created", false)]
    [InlineData(null, false)]
    public void IsSyncTrigger_OnlyForEventsCarryingData(string? type, bool expected) =>
        QuilttFinancialDataProvider.IsSyncTrigger(type).Should().Be(expected);

    /// <summary>Opt-in: runs the real provider against Quiltt's sandbox. Set QUILTT_LIVE_KEY and QUILTT_LIVE_PROFILE.</summary>
    [Fact]
    public async Task Live_Sandbox_ReadsAccountsAndTransactions()
    {
        var key = Environment.GetEnvironmentVariable("QUILTT_LIVE_KEY");
        var profile = Environment.GetEnvironmentVariable("QUILTT_LIVE_PROFILE");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(profile)) { return; }

        var opts = new QuilttOptions { ApiKeySecret = key, ConnectorId = "live", InitialHistoryDays = 5000 };
        var o = Options.Create(opts);
        var p = new QuilttFinancialDataProvider(new QuilttApiClient(new HttpClient(), o), o, null, NullLogger<QuilttFinancialDataProvider>.Instance);

        (await p.GetConnectionStatusAsync(profile)).Should().Be(ConnectionStatus.Healthy);
        var accounts = await p.GetAccountsAsync(profile);
        accounts.Should().NotBeEmpty();
        accounts.Should().OnlyContain(a => a.Name.StartsWith("Mock Sandbox Bank"));

        var total = 0;
        foreach (var a in accounts)
        {
            var page = await p.GetTransactionsAsync(a.ProviderId, SyncCursor.Initial);
            total += page.Items.Count;
        }

        total.Should().BeGreaterThan(0);
    }

    // ── Webhooks ──

    private static string Header(string secret, string body, long ts)
    {
        // Quiltt: Base64(HMAC-SHA256(secret, "1" + Quiltt-Timestamp + body)); the endpoint joins both headers as t=,v1=.
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"1{ts}{body}"));
        return $"t={ts},v1={Convert.ToBase64String(mac)}";
    }

    private const string Hook = """{"environment":{"id":"env_1","mode":"SANDBOX"},"eventTypes":["connection.synced.successful"],"events":[{"id":"evt_1","at":"2026-11-25T18:33:03Z","type":"connection.synced.successful","profile":{"id":"p_1","uuid":"u"},"record":{"id":"conn_1","status":"SYNCED"}}]}""";

    [Fact]
    public async Task Webhook_ValidSignature_VerifiesAndParses()
    {
        var p = Make(HappyPath());
        var raw = Encoding.UTF8.GetBytes(Hook);
        var header = Header("whsec", Hook, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await p.VerifyWebhookAsync(raw, header);
        var evt = await p.ParseWebhookAsync(raw);

        evt.EventType.Should().Be("connection.synced.successful");
        evt.MemberId.Should().Be("p_1");
        evt.Metadata["recordId"].Should().Be("conn_1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=1,v1=deadbeef")]
    public async Task Webhook_BadSignature_Rejected(string header)
    {
        var p = Make(HappyPath());
        await p.Invoking(x => x.VerifyWebhookAsync(Encoding.UTF8.GetBytes(Hook), header))
            .Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    [Fact]
    public async Task Webhook_WrongSecret_And_StaleTimestamp_Rejected()
    {
        var p = Make(HappyPath());
        var raw = Encoding.UTF8.GetBytes(Hook);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await p.Invoking(x => x.VerifyWebhookAsync(raw, Header("other", Hook, now)))
            .Should().ThrowAsync<InvalidWebhookSignatureException>();
        await p.Invoking(x => x.VerifyWebhookAsync(raw, Header("whsec", Hook, now - 3600)))
            .Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    [Fact]
    public async Task Webhook_NoSecretConfigured_RefusesEverything()
    {
        var p = Make(HappyPath(), webhookSecret: null);
        await p.Invoking(x => x.VerifyWebhookAsync(Encoding.UTF8.GetBytes(Hook), "t=1,v1=aa"))
            .Should().ThrowAsync<InvalidWebhookSignatureException>();
    }
}
