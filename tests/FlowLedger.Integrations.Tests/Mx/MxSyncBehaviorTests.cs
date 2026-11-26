using System.Text;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx;
using FlowLedger.Integrations.Mx.Mapping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Verifies the post-widget-connect sync path: <c>BeginConnectionAsync</c> (non-widget) must
/// discover widget-created members via <c>GET /users/{g}/members</c> rather than calling
/// <c>POST /users/{g}/members</c>. Tests cover happy-path, no-members, still-aggregating,
/// and CHALLENGED scenarios, plus regression proof that <c>POST .../members</c> is never hit.
/// </summary>
[Trait("Provider", "MX")]
public sealed class MxSyncBehaviorTests : IDisposable
{
    private const string Vnd = "application/vnd.mx.api.v1+json";
    private const string UserGuid = "USR-sync";
    private const string MemberGuid = "MBR-sync";
    private const string AccountGuid = "ACT-sync";

    private readonly WireMockServer _server;

    public MxSyncBehaviorTests()
    {
        _server = WireMockServer.Start(new WireMockServerSettings { StartAdminInterface = false });
    }

    public void Dispose() => _server.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string Obj(string inner) => "{" + inner + "}";
    private static string Arr(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";

    private static string UserJson() =>
        Obj($"\"user\":{Obj($"\"guid\":\"{UserGuid}\",\"id\":\"flowledger-test\"")}");

    private static string MemberJson(string status, bool isBeingAggregated = false) =>
        Obj($"\"member\":{MemberObj(status, isBeingAggregated)}");

    private static string MembersListJson(
        IEnumerable<(string Guid, string Name, string Status, bool IsBeingAggregated)> members,
        int totalPages = 1)
    {
        var memberObjs = members.Select(m =>
            Obj($"\"guid\":\"{m.Guid}\"," +
                $"\"name\":\"{m.Name}\"," +
                "\"institution_code\":\"syncbank\"," +
                $"\"connection_status\":\"{m.Status}\"," +
                "\"connection_status_message\":null," +
                $"\"is_being_aggregated\":{(m.IsBeingAggregated ? "true" : "false")}," +
                $"\"user_guid\":\"{UserGuid}\""));
        var pagination = Obj($"\"current_page\":1,\"per_page\":100,\"total_entries\":{totalPages},\"total_pages\":{totalPages}");
        return Obj($"\"members\":{Arr(memberObjs)},\"pagination\":{pagination}");
    }

    private static string EmptyMembersListJson() =>
        Obj("\"members\":[],\"pagination\":{\"current_page\":1,\"per_page\":100,\"total_entries\":0,\"total_pages\":0}");

    private static string MemberObj(string status, bool isBeingAggregated = false) =>
        Obj($"\"guid\":\"{MemberGuid}\"," +
            "\"name\":\"Sync Bank\"," +
            "\"institution_code\":\"syncbank\"," +
            $"\"connection_status\":\"{status}\"," +
            "\"connection_status_message\":null," +
            $"\"is_being_aggregated\":{(isBeingAggregated ? "true" : "false")}," +
            $"\"user_guid\":\"{UserGuid}\"");

    private static string AccountsJson() =>
        Obj($"\"accounts\":{Arr([Obj($"\"guid\":\"{AccountGuid}\",\"name\":\"Sync Checking\"," +
            "\"type\":\"CHECKING\",\"balance\":1000.00,\"currency_code\":\"USD\"," +
            $"\"member_guid\":\"{MemberGuid}\",\"user_guid\":\"{UserGuid}\"")])}" +
            ",\"pagination\":{\"current_page\":1,\"per_page\":25,\"total_entries\":1,\"total_pages\":1}");

    private static string TransactionsJson() =>
        Obj($"\"transactions\":{Arr([Obj($"\"guid\":\"TXN-1\",\"account_guid\":\"{AccountGuid}\"," +
            "\"amount\":42.00,\"currency_code\":\"USD\",\"date\":\"2026-01-15\"," +
            "\"posted_at\":\"2026-01-15T12:00:00Z\",\"transacted_at\":\"2026-01-15T12:00:00Z\"," +
            "\"description\":\"Coffee Shop\",\"original_description\":\"COFFEE SHOP RAW\"," +
            "\"status\":\"POSTED\",\"type\":\"DEBIT\"")])}" +
            ",\"pagination\":{\"current_page\":1,\"per_page\":25,\"total_entries\":1,\"total_pages\":1}");

    /// <summary>
    /// Builds a provider wired to the WireMock server with poll interval = 0 so status polls
    /// complete immediately.
    /// </summary>
    private MxFinancialDataProvider CreateProvider(int pollIntervalMs = 0, int maxPollAttempts = 10, int maxPages = 100)
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!, UriKind.Absolute) };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-client:test-key"));
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(Vnd));

        var apiClient = new MxApiClient(http, NullLogger<MxApiClient>.Instance, new InMemoryRawPayloadArchive());
        var verifier = new MxWebhookVerifier("test-secret");
        var options = Options.Create(new MxProviderOptions
        {
            RecordsPerPage = 25,
            DefaultInstitutionCode = "syncbank",
            PollIntervalMs = pollIntervalMs,
            MaxPollAttempts = maxPollAttempts,
            MaxPages = maxPages,
        });

        return new MxFinancialDataProvider(
            apiClient, verifier, options, NullLogger<MxFinancialDataProvider>.Instance);
    }

    private static TenantId TestTenant =>
        TenantId.From(new Guid("00000000-0000-0000-0000-000000000002"));

    /// <summary>
    /// Provisioning is recovery-first: GET /users (lookup by external id) finds nothing, then
    /// POST /users creates the user.
    /// </summary>
    private void StubCreateUser()
    {
        _server.Given(Request.Create().WithPath("/users").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody("{\"users\":[],\"pagination\":{\"current_page\":1,\"per_page\":100,\"total_entries\":0,\"total_pages\":1}}"));
        _server.Given(Request.Create().WithPath("/users").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd).WithBody(UserJson()));
    }

    private void StubMembersList(string status, bool isBeingAggregated = false) =>
        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/members").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(MembersListJson([(MemberGuid, "Sync Bank", status, isBeingAggregated)])));

    private void StubEmptyMembersList() =>
        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/members").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(EmptyMembersListJson()));

    private void StubMemberStatus(string status, bool isBeingAggregated = false) =>
        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/members/{MemberGuid}/status").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(MemberJson(status, isBeingAggregated)));

    private void StubAccounts() =>
        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/members/{MemberGuid}/accounts").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(AccountsJson()));

    private void StubTransactions() =>
        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/accounts/{AccountGuid}/transactions").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(TransactionsJson()));

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Happy path: one CONNECTED, non-aggregating member. Sync should discover it via
    /// GET /users/{g}/members, return a valid member ref, and accounts should load.
    /// POST /users/{g}/members must be called ZERO times.
    /// </summary>
    [Fact]
    public async Task HappySync_discovers_connected_member_without_calling_CreateMember()
    {
        StubCreateUser();
        StubMembersList("CONNECTED", isBeingAggregated: false);
        StubMemberStatus("CONNECTED", isBeingAggregated: false); // GetAccountsAsync calls status
        StubAccounts();
        StubTransactions();

        var provider = CreateProvider();

        // BeginConnectionAsync should return a valid member ref
        var memberRef = await provider.BeginConnectionAsync(TestTenant);

        memberRef.ProviderId.Should().NotBeNullOrWhiteSpace("member ref must carry a packed composite id");
        memberRef.ProviderId.Should().Contain("|", "composite id must pack userGuid|memberGuid");
        memberRef.Status.Should().Be(ConnectionStatus.Connected);
        memberRef.InstitutionName.Should().Be("Sync Bank");

        // GetAccountsAsync should work using the discovered member ref
        var accounts = await provider.GetAccountsAsync(memberRef.ProviderId);
        accounts.Should().HaveCount(1);
        accounts[0].Name.Should().Be("Sync Checking");

        // POST /users/{guid}/members must NEVER be called in the sync path
        var createMemberRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path == $"/users/{UserGuid}/members" &&
            string.Equals(e.RequestMessage.Method, "POST", StringComparison.OrdinalIgnoreCase));

        createMemberRequests.Should().Be(0,
            "sync path must NOT call POST /users/{guid}/members — MX rejects it without credentials; " +
            "the widget-created member must be discovered via GET instead");

        // GET /users/{guid}/members must have been called (discovery)
        var listMemberRequests = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path == $"/users/{UserGuid}/members" &&
            string.Equals(e.RequestMessage.Method, "GET", StringComparison.OrdinalIgnoreCase));

        listMemberRequests.Should().BeGreaterThanOrEqualTo(1,
            "sync path must call GET /users/{guid}/members to discover the widget-created member");
    }

    /// <summary>
    /// No members yet: GET /users/{g}/members returns an empty list.
    /// Must surface as NeedsUserActionProviderException (ActionRequired), NOT a fatal error.
    /// </summary>
    [Fact]
    public async Task NoMembers_throws_NeedsUserAction_not_FatalProviderException()
    {
        StubCreateUser();
        StubEmptyMembersList();

        var provider = CreateProvider();

        var act = async () => await provider.BeginConnectionAsync(TestTenant);

        // Must throw NeedsUserActionProviderException (maps to ActionRequired in FinancialSyncService)
        // NOT FatalProviderException (which would surface as a red "Last sync failed: ..." message).
        var ex = await act.Should().ThrowAsync<NeedsUserActionProviderException>(
            "no members means the user hasn't connected a bank yet — this is an ActionRequired condition, " +
            "not a fatal failure that should show a red error to the user");

        ex.Which.Message.ToUpperInvariant().Should().Contain("NO FINANCIAL INSTITUTION CONNECTED");
    }

    /// <summary>
    /// Member still aggregating (is_being_aggregated: true), then becomes ready on second poll.
    /// Sync must succeed eventually. Poll interval is 0 to avoid real delays.
    /// </summary>
    [Fact]
    public async Task StillAggregating_member_becomes_ready_after_poll_and_sync_succeeds()
    {
        StubCreateUser();

        // Members list returns member that IS being aggregated.
        StubMembersList("CONNECTED", isBeingAggregated: true);

        // Status endpoint: first poll returns still aggregating, second returns ready.
        const string statusPath = $"/users/{UserGuid}/members/{MemberGuid}/status";
        const string scenario = "aggregation-completes";

        _server
            .Given(Request.Create().WithPath(statusPath).UsingGet())
            .InScenario(scenario)
            .WillSetStateTo("ready")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                .WithBody(MemberJson("CONNECTED", isBeingAggregated: true)));

        _server
            .Given(Request.Create().WithPath(statusPath).UsingGet())
            .InScenario(scenario)
            .WhenStateIs("ready")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                .WithBody(MemberJson("CONNECTED", isBeingAggregated: false)));

        // Also stub accounts/transactions for the final GetAccountsAsync call
        // (which itself calls status again — stub that too for safety)
        StubAccounts();
        StubTransactions();

        var provider = CreateProvider(pollIntervalMs: 0, maxPollAttempts: 10);

        // Must NOT throw — member becomes ready after one additional poll
        var memberRef = await provider.BeginConnectionAsync(TestTenant);

        memberRef.ProviderId.Should().NotBeNullOrWhiteSpace();
        memberRef.Status.Should().Be(ConnectionStatus.Connected);

        // Status endpoint must have been polled at least twice (initial transient + ready)
        var statusPollCount = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path == statusPath &&
            string.Equals(e.RequestMessage.Method, "GET", StringComparison.OrdinalIgnoreCase));

        statusPollCount.Should().BeGreaterThanOrEqualTo(2,
            "provider must poll the status endpoint until the member is ready");
    }

    /// <summary>
    /// CHALLENGED member: must throw NeedsUserActionProviderException (ActionRequired), not fatal.
    /// The user needs to re-open the widget to complete MFA.
    /// </summary>
    [Fact]
    public async Task ChallengedMember_throws_NeedsUserAction_not_fatal()
    {
        StubCreateUser();
        StubMembersList("CHALLENGED", isBeingAggregated: false);

        var provider = CreateProvider();

        var act = async () => await provider.BeginConnectionAsync(TestTenant);

        var ex = await act.Should().ThrowAsync<NeedsUserActionProviderException>(
            "a CHALLENGED member requires user re-auth via the widget — this is ActionRequired, not a fatal failure");

        ex.Which.Message.ToUpperInvariant().Should().Contain("VERIFICATION",
            "the message should hint that additional verification is needed");

        // Confirm it's not a FatalProviderException (which would cause a red UI error)
        ex.Which.Should().BeOfType<NeedsUserActionProviderException>();
    }

    /// <summary>
    /// All members are terminal (FAILED). Must throw NeedsUserActionProviderException
    /// with a reconnect hint — not a fatal error.
    /// </summary>
    [Fact]
    public async Task AllTerminalMembers_throws_NeedsUserAction_with_reconnect_hint()
    {
        StubCreateUser();

        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/members").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(MembersListJson([(MemberGuid, "Sync Bank", "FAILED", false)])));

        var provider = CreateProvider();

        var act = async () => await provider.BeginConnectionAsync(TestTenant);

        var ex = await act.Should().ThrowAsync<NeedsUserActionProviderException>(
            "terminal members mean the connection is dead — requires user reconnect, not a fatal crash");

        ex.Which.Message.ToUpperInvariant().Should().Contain("RE-ESTABLISHED",
            "the message should tell the user to reconnect");
    }

    /// <summary>
    /// Aggregation poll cap reached: member never becomes ready within MaxPollAttempts.
    /// Must throw a retryable TransientProviderException, not hang forever.
    /// </summary>
    [Fact]
    public async Task StillAggregatingAfterPollCap_throws_retryable_transient()
    {
        StubCreateUser();

        // Members list returns member that IS being aggregated.
        StubMembersList("CONNECTED", isBeingAggregated: true);

        // Status endpoint ALWAYS returns still-aggregating (never completes).
        StubMemberStatus("CONNECTED", isBeingAggregated: true);

        var provider = CreateProvider(pollIntervalMs: 0, maxPollAttempts: 3);

        var act = async () => await provider.BeginConnectionAsync(TestTenant);

        var ex = await act.Should().ThrowAsync<TransientProviderException>(
            "when the poll cap is reached, the provider must give up and surface a retryable condition, not hang");

        ex.Which.Message.ToUpperInvariant().Should().Contain("STILL CONNECTING");
        ex.Which.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
        var issue = ProviderIssue.FromException(ex.Which);
        issue.Retryable.Should().BeTrue();
        issue.RequiresUserAction.Should().BeFalse("a connection that is still aggregating is healthy and must keep being retried");

        // Must have polled exactly maxPollAttempts times
        var statusPath = $"/users/{UserGuid}/members/{MemberGuid}/status";
        var pollCount = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path == statusPath &&
            string.Equals(e.RequestMessage.Method, "GET", StringComparison.OrdinalIgnoreCase));

        pollCount.Should().Be(3, "provider must poll exactly MaxPollAttempts times before giving up");
    }

    /// <summary>
    /// Widget path (BeginConnectionWithWidgetAsync) must NOT POST a member — the widget creates
    /// it asynchronously after the user authenticates. With no member yet it returns a PENDING
    /// placeholder (adopting an existing member, if any, is covered by MxProvisionRecoveryTests).
    /// Regression guard: widget path must stay untouched by the sync-path fix.
    /// </summary>
    [Fact]
    public async Task WidgetPath_returns_pending_member_and_does_not_call_CreateMember()
    {
        StubCreateUser();
        StubEmptyMembersList();

        // Widget URL stub
        const string widgetJson =
            "{\"widget_url\":{\"url\":\"https://example.com/widget\",\"type\":\"connect_widget\",\"user_id\":\"test\"}}";
        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/widget_urls").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(widgetJson));

        var provider = CreateProvider();

        var (memberRef, widgetUrl) = await provider.BeginConnectionWithWidgetAsync(TestTenant);

        // Widget path must return a pending/sentinel member ref (no real member yet)
        memberRef.Status.Should().Be(ConnectionStatus.ConnectionPending,
            "widget path returns a pending sentinel — the member is created by the widget, not yet");

        widgetUrl.Should().StartWith("https://", "widget URL must be returned");

        // POST /users/{guid}/members must NOT be called
        var createMemberCalls = _server.LogEntries.Count(e =>
            e.RequestMessage?.Path == $"/users/{UserGuid}/members" &&
            string.Equals(e.RequestMessage.Method, "POST", StringComparison.OrdinalIgnoreCase));
        createMemberCalls.Should().Be(0, "widget path must not call CreateMember");

        memberRef.ProviderId.Should().Be($"{UserGuid}|PENDING",
            "the placeholder is resolved to the widget-created member on first read");
    }

    /// <summary>
    /// First sync after a widget connect (persisted id is the PENDING placeholder): the resolved
    /// member is still aggregating, so GetAccountsAsync waits until it is ready before reading.
    /// </summary>
    [Fact]
    public async Task PendingMember_first_sync_waits_for_aggregation_then_reads_accounts()
    {
        StubMembersList("CONNECTED", isBeingAggregated: true);

        const string statusPath = $"/users/{UserGuid}/members/{MemberGuid}/status";
        const string scenario = "pending-aggregation";
        _server.Given(Request.Create().WithPath(statusPath).UsingGet())
               .InScenario(scenario).WillSetStateTo("ready")
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(MemberJson("CONNECTED", isBeingAggregated: true)));
        _server.Given(Request.Create().WithPath(statusPath).UsingGet())
               .InScenario(scenario).WhenStateIs("ready")
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(MemberJson("CONNECTED", isBeingAggregated: false)));
        StubAccounts();

        var accounts = await CreateProvider().GetAccountsAsync($"{UserGuid}|PENDING");

        accounts.Should().HaveCount(1);
        _server.LogEntries.Count(e => e.RequestMessage?.Path == statusPath).Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>A PENDING member that is CHALLENGED surfaces NeedsUserAction on first sync.</summary>
    [Fact]
    public async Task PendingMember_challenged_throws_NeedsUserAction()
    {
        StubMembersList("CHALLENGED");
        StubMemberStatus("CHALLENGED");

        var act = async () => await CreateProvider().GetAccountsAsync($"{UserGuid}|PENDING");

        (await act.Should().ThrowAsync<NeedsUserActionProviderException>())
            .Which.Message.ToUpperInvariant().Should().Contain("VERIFICATION");
    }

    [Fact]
    public async Task Transactions_pass_stops_at_MaxPages_without_writing_a_watermark()
    {
        _server.Given(Request.Create().WithPath($"/users/{UserGuid}/accounts/{AccountGuid}/transactions").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", Vnd)
                   .WithBody(Obj($"\"transactions\":{Arr([Obj($"\"guid\":\"TXN-1\",\"account_guid\":\"{AccountGuid}\"," +
                       "\"amount\":42.00,\"currency_code\":\"USD\",\"date\":\"2026-01-15\"," +
                       "\"description\":\"Coffee Shop\",\"status\":\"POSTED\",\"type\":\"DEBIT\"")])}" +
                       ",\"pagination\":{\"current_page\":1,\"per_page\":25,\"total_entries\":75,\"total_pages\":3}")));

        var page = await CreateProvider(maxPages: 1)
            .GetTransactionsAsync($"{UserGuid}|{AccountGuid}", SyncCursor.Initial, 25);

        page.HasMore.Should().BeFalse("the bound ends the pass instead of looping forever");
        var next = MxCursor.Decode(page.NextCursor, 25, NullLogger.Instance);
        next.Page.Should().Be(2, "the next sync resumes at the next page; no date watermark may be written on a truncated pass");
        next.FromDate.Should().BeNull();
    }
}
