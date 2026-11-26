using System.Net;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Connections;
using FluentAssertions;
using MudBlazor;

namespace FlowLedger.Web.Tests;

public sealed class ConnectionErrorHandlingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // kind, retryable, requiresUserAction, expected category, tone, reconnect button
    public static TheoryData<string, bool, bool, ConnectionIssueCategory, HealthTone, bool> Kinds => new()
    {
        { "ReauthRequired", false, true, ConnectionIssueCategory.UserAction, HealthTone.Warning, true },
        { "InvalidCredentials", false, true, ConnectionIssueCategory.UserAction, HealthTone.Warning, true },
        { "Disconnected", false, true, ConnectionIssueCategory.UserAction, HealthTone.Warning, true },
        { "InstitutionUnavailable", true, false, ConnectionIssueCategory.Transient, HealthTone.Info, false },
        { "ProviderUnavailable", true, false, ConnectionIssueCategory.Transient, HealthTone.Info, false },
        { "ServiceUnavailable", true, false, ConnectionIssueCategory.Transient, HealthTone.Info, false },
        { "RateLimited", true, false, ConnectionIssueCategory.Transient, HealthTone.Info, false },
        { "Internal", false, false, ConnectionIssueCategory.Fault, HealthTone.Error, false },
        { "Unknown", false, false, ConnectionIssueCategory.Fault, HealthTone.Error, false },
        { "None", false, false, ConnectionIssueCategory.Healthy, HealthTone.Success, false },
        // Kind drives the category even if the API omitted the flags.
        { "ReauthRequired", false, false, ConnectionIssueCategory.UserAction, HealthTone.Warning, true },
        { "RateLimited", false, false, ConnectionIssueCategory.Transient, HealthTone.Info, false },
        // A future kind the UI does not know is classified from the flags.
        { "BrandNewKind", true, false, ConnectionIssueCategory.Transient, HealthTone.Info, false },
        { "BrandNewKind", false, true, ConnectionIssueCategory.UserAction, HealthTone.Warning, true },
        { "BrandNewKind", false, false, ConnectionIssueCategory.Fault, HealthTone.Error, false },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Badge_view_for_every_issue_kind(string kind, bool retryable, bool user, ConnectionIssueCategory cat, HealthTone tone, bool reconnect)
    {
        var view = ConnectionHealth.Describe(new ConnectionIssueDto(kind, "m", retryable, user), Now);

        view.Category.Should().Be(cat);
        view.Tone.Should().Be(tone);
        view.CanReconnect.Should().Be(reconnect);
        if (cat == ConnectionIssueCategory.Transient)
        {
            view.Label.Should().Be("Temporarily unavailable - retrying automatically");
        }
    }

    [Fact]
    public void Null_issue_is_healthy()
        => ConnectionHealth.Describe((ConnectionIssueDto?)null, Now).Category.Should().Be(ConnectionIssueCategory.Healthy);

    [Fact]
    public void Transient_detail_includes_retry_time_when_present()
    {
        var view = ConnectionHealth.Describe(new ConnectionIssueDto("ProviderUnavailable", Retryable: true, RetryAfter: Now.AddMinutes(10)), Now);
        view.Detail.Should().Contain("in 10 min");
        view.RetryAt.Should().Be(Now.AddMinutes(10));
    }

    [Theory]
    [InlineData(10, "shortly")]
    [InlineData(300, "in 5 min")]
    [InlineData(7200, "in 2 h")]
    public void RetryText_is_relative(int seconds, string expected)
        => ConnectionHealth.RetryText(Now.AddSeconds(seconds), Now).Should().Be(expected);

    [Fact]
    public void Worst_prefers_user_action_over_transient()
    {
        var links = new List<ConnectorLinkDto>
        {
            new("A", "ok", Now, Now, null),
            new("B", "x", Now, Now, new ConnectionIssueDto("RateLimited", Retryable: true)),
            new("C", "x", Now, Now, new ConnectionIssueDto("ReauthRequired", RequiresUserAction: true)),
        };
        ConnectionHealth.Worst(links)!.Kind.Should().Be("ReauthRequired");
        ConnectionHealth.Worst([links[0]]).Should().BeNull();
    }

    // ---- problem extension reader ----

    [Fact]
    public void Problem_extensions_are_read_from_problem_details()
    {
        var doc = ProblemDocument.Parse("""
            {"title":"Reconnect","detail":"Sign in again.","status":409,
             "issueKind":"ReauthRequired","retryable":false,"requiresUserAction":true,"retryAfterSeconds":120,"provider":"Quiltt"}
            """);

        doc.Detail.Should().Be("Sign in again.");
        doc.Extensions.Should().Be(new ProblemExtensions("ReauthRequired", false, true, 120, "Quiltt"));
        doc.Extensions!.RetryAfter.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"title":"Bad request","errors":{"Name":["Required."]}}""")]
    public void Problem_without_extensions_yields_none(string body)
        => ProblemDocument.Parse(body).Extensions.Should().BeNull();

    [Fact]
    public void Validation_errors_are_summarised()
        => ProblemDocument.Parse("""{"errors":{"A":["one"],"B":["two"]}}""").ValidationMessage.Should().Be("one two");

    [Fact]
    public void Exception_exposes_typed_extensions()
    {
        var ex = new ApiClientException("m", HttpStatusCode.TooManyRequests, problem: new ProblemExtensions("RateLimited", true, false, 30, "SimpleFIN"));
        ex.IssueKind.Should().Be("RateLimited");
        ex.Retryable.Should().BeTrue();
        ex.RequiresUserAction.Should().BeFalse();
        ex.RetryAfterSeconds.Should().Be(30);
        ex.Provider.Should().Be("SimpleFIN");
        new ApiClientException("plain").Problem.Should().BeNull();
    }

    // ---- repair handler selection ----

    private sealed class FakeHandler(string provider, RepairStatus status) : IConnectionRepairHandler
    {
        public ConnectionRef? Seen { get; private set; }
        public bool CanRepair(string p) => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase);
        public Task<RepairResult> RepairAsync(ConnectionRef c, CancellationToken ct = default)
        {
            Seen = c;
            return Task.FromResult(new RepairResult(status));
        }
    }

    [Fact]
    public async Task Repair_service_dispatches_to_matching_handler()
    {
        var a = new FakeHandler("Alpha", RepairStatus.Repaired);
        var b = new FakeHandler("Beta", RepairStatus.Cancelled);
        var svc = new ConnectionRepairService([a, b]);

        var result = await svc.RepairAsync(new ConnectionRef("beta", "c1", "Bank"));

        result.Status.Should().Be(RepairStatus.Cancelled);
        b.Seen!.ConnectionId.Should().Be("c1");
        a.Seen.Should().BeNull();
    }

    [Fact]
    public async Task Repair_service_falls_back_to_instructions_without_a_handler()
    {
        var svc = new ConnectionRepairService([new FakeHandler("Alpha", RepairStatus.Repaired)]);

        var result = await svc.RepairAsync(new ConnectionRef("Other", null, "My Bank"));

        result.Status.Should().Be(RepairStatus.NotSupported);
        result.Message.Should().Contain("My Bank").And.Contain("Other");
    }

    [Fact]
    public void Quiltt_handler_only_claims_its_provider()
    {
        var h = new QuilttRepairHandler(null!, null!);
        h.CanRepair("quiltt").Should().BeTrue();
        h.CanRepair("SimpleFIN").Should().BeFalse();
    }

    // ---- toast helper ----

    private static ApiClientException Problem(string kind, bool retryable, bool user, int? retryAfter = null)
        => new("server said no", HttpStatusCode.Conflict, problem: new ProblemExtensions(kind, retryable, user, retryAfter, "P"));

    [Fact]
    public void Toast_for_user_action_offers_reconnect_as_warning()
    {
        var t = ProblemToast.From(Problem("ReauthRequired", false, true), "Sync", Now);
        t.OfferReconnect.Should().BeTrue();
        t.Severity.Should().Be(Severity.Warning);
    }

    [Fact]
    public void Toast_for_retryable_is_informational()
    {
        var t = ProblemToast.From(Problem("InstitutionUnavailable", true, false), "Sync", Now);
        t.OfferReconnect.Should().BeFalse();
        t.Severity.Should().Be(Severity.Info);
        t.Message.Should().Contain("retrying automatically");
    }

    [Fact]
    public void Toast_for_rate_limit_shows_when_to_retry()
    {
        var t = ProblemToast.From(Problem("RateLimited", true, false, 300), "Sync", Now);
        t.Severity.Should().Be(Severity.Info);
        t.Message.Should().Contain("in 5 min");
    }

    [Fact]
    public void Toast_for_internal_or_plain_errors_is_an_error_with_message()
    {
        ProblemToast.From(Problem("Internal", false, false), "Sync", Now).Severity.Should().Be(Severity.Error);
        var plain = ProblemToast.From(new InvalidOperationException("boom"), "Connect", Now);
        plain.Severity.Should().Be(Severity.Error);
        plain.Message.Should().Be($"Connect failed: {FlowLedger.Web.Shared.SnackbarExtensions.GenericFailure}");
    }

    // ---- auto-sync policy ----

    [Fact]
    public void Auto_sync_stops_when_user_action_required()
    {
        var d = AutoSyncPolicy.AfterFailure(Problem("ReauthRequired", false, true), Now, TimeSpan.FromHours(2));
        d.StopUntilUserActs.Should().BeTrue();
    }

    [Fact]
    public void Auto_sync_honours_retry_after_and_caps_it()
    {
        AutoSyncPolicy.AfterFailure(Problem("RateLimited", true, false, 600), Now, TimeSpan.FromHours(2))
            .BackoffUntil.Should().Be(Now.AddMinutes(10));
        AutoSyncPolicy.AfterFailure(Problem("RateLimited", true, false, 999_999), Now, TimeSpan.FromHours(2))
            .BackoffUntil.Should().Be(Now.AddHours(24));
    }

    [Fact]
    public void Auto_sync_uses_default_backoff_otherwise()
    {
        var d = AutoSyncPolicy.AfterFailure(new InvalidOperationException(), Now, TimeSpan.FromHours(2));
        d.BackoffUntil.Should().Be(Now.AddHours(2));
        d.StopUntilUserActs.Should().BeFalse();
    }
}

public sealed class ConnectExitTests
{
    [Theory]
    [InlineData("success", FlowLedger.Web.Connections.ConnectOutcome.Success)]
    [InlineData("SUCCESS", FlowLedger.Web.Connections.ConnectOutcome.Success)]
    [InlineData("cancelled", FlowLedger.Web.Connections.ConnectOutcome.Cancelled)]
    [InlineData("canceled", FlowLedger.Web.Connections.ConnectOutcome.Cancelled)]
    [InlineData("error", FlowLedger.Web.Connections.ConnectOutcome.Failed)]
    [InlineData("", FlowLedger.Web.Connections.ConnectOutcome.Failed)]
    [InlineData(null, FlowLedger.Web.Connections.ConnectOutcome.Failed)]
    [InlineData("something-new", FlowLedger.Web.Connections.ConnectOutcome.Failed)]
    public void Parse_NormalisesAnyWidgetExit(string? status, FlowLedger.Web.Connections.ConnectOutcome expected) =>
        FlowLedger.Web.Connections.ConnectExit.Parse(status).Should().Be(expected);

    [Fact]
    public void EveryOutcome_HasADistinctActionableMessage_WithoutVendorNames()
    {
        var messages = Enum.GetValues<FlowLedger.Web.Connections.ConnectOutcome>()
            .Select(o => FlowLedger.Web.Connections.ConnectExit.Message(o)!.Value).ToList();

        messages.Select(m => m.Message).Distinct().Should().HaveCount(3);
        messages.Should().OnlyContain(m => !m.Message.Contains("Quiltt", StringComparison.OrdinalIgnoreCase));
        FlowLedger.Web.Connections.ConnectExit.Message(FlowLedger.Web.Connections.ConnectOutcome.Failed)!.Value.Message
            .Should().Contain("nothing was linked").And.Contain("Try again");
    }
}

public sealed class MyConnectionsClientTests
{
    private sealed class Stub(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task GetMyConnectionsAsync_ReadsTheIssueAndRepairRef()
    {
        const string json = """[{"provider":"Quiltt","institution":"Mock","status":"ConnectionPending","issue":{"kind":"ReauthRequired","message":"m","retryable":false,"requiresUserAction":true,"retryAfter":null,"at":"2026-11-25T12:00:00Z","consecutiveFailures":2,"providerCode":"X","repairRef":"conn_1"}},{"provider":"Other","issue":null}]""";
        var client = new FlowLedgerApiClient(
            new HttpClient(new Stub(json)) { BaseAddress = new Uri("http://api.test") },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowLedgerApiClient>.Instance);

        var connections = await client.GetMyConnectionsAsync();

        connections.Should().HaveCount(2);
        connections[0].Issue!.RepairRef.Should().Be("conn_1");
        connections[0].Issue!.RequiresUserAction.Should().BeTrue();
        connections[1].Issue.Should().BeNull();
    }
}
