using System.Net;
using System.Text.Json;
using FlowLedger.Application.Abstractions;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>
/// ADR 0018, HTTP row. A fake connector throws every <see cref="ProviderIssueKind"/> through a real endpoint; the mapping must
/// depend only on the connector-neutral contract, never on which connector (or native status) produced the failure.
/// </summary>
[Collection("ApiIntegration")]
public sealed class ProviderErrorHandlingTests(ProviderFailureApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        factory.Failure = null;
        factory.Connections = [];
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<ProviderIssueKind, int, bool, bool, bool> KindMatrix => new()
    {
        // kind, status, retryable, requiresUserAction, retryAfterExpected
        { ProviderIssueKind.ReauthRequired, 409, false, true, false },
        { ProviderIssueKind.InvalidCredentials, 409, false, true, false },
        { ProviderIssueKind.Disconnected, 409, false, true, false },
        { ProviderIssueKind.InstitutionUnavailable, 503, true, false, true },
        { ProviderIssueKind.ProviderUnavailable, 503, true, false, true },
        { ProviderIssueKind.ServiceUnavailable, 503, true, false, true },
        { ProviderIssueKind.RateLimited, 429, true, false, true },
        { ProviderIssueKind.Internal, 502, true, false, false },
        { ProviderIssueKind.Unknown, 502, false, false, false },
    };

    [Theory]
    [MemberData(nameof(KindMatrix))]
    public async Task Every_issue_kind_maps_to_the_same_problem_shape_regardless_of_connector(
        ProviderIssueKind kind, int status, bool retryable, bool requiresUserAction, bool withRetryAfter)
    {
        // Any exception type, any native code: only Kind (and RetryAfter) may matter.
        factory.Failure = withRetryAfter
            ? new RateLimitedProviderException("Please slow down.", DateTimeOffset.UtcNow.AddSeconds(90), "native-xyz") { Kind = kind }
            : new FatalProviderException("Something is wrong.", "native-xyz") { Kind = kind };

        var response = await _client.PostAsync("/api/sync", null);

        ((int)response.StatusCode).Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(status);
        root.GetProperty("issueKind").GetString().Should().Be(kind.ToString());
        root.GetProperty("retryable").GetBoolean().Should().Be(ProviderIssue.IsRetryableByDefault(kind)).And.Be(retryable);
        root.GetProperty("requiresUserAction").GetBoolean().Should().Be(requiresUserAction);
        root.GetProperty("detail").GetString().Should().NotBeNullOrEmpty().And.NotContain("native-xyz");

        if (withRetryAfter)
        {
            root.GetProperty("retryAfterSeconds").GetInt32().Should().BeInRange(85, 91);
            response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(85, 91);
        }
        else
        {
            root.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse();
            response.Headers.Contains("Retry-After").Should().BeFalse();
        }
    }

    [Fact]
    public async Task Transient_failure_with_a_delay_returns_503_and_retry_after()
    {
        factory.Failure = new TransientProviderException("Upstream is down.", TimeSpan.FromSeconds(120));

        var response = await _client.PostAsync("/api/sync", null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(115, 121);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("issueKind").GetString().Should().Be(nameof(ProviderIssueKind.ServiceUnavailable));
    }

    [Fact]
    public async Task Needs_user_action_prefers_the_user_hint_as_detail()
    {
        factory.Failure = new NeedsUserActionProviderException("login required", userHint: "Reconnect your bank.");

        var response = await _client.PostAsync("/api/sync", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("detail").GetString().Should().Be("Reconnect your bank.");
        doc.RootElement.GetProperty("issueKind").GetString().Should().Be(nameof(ProviderIssueKind.ReauthRequired));
    }

    [Fact]
    public async Task Call_budget_exhaustion_stays_429()
    {
        factory.Failure = new ProviderCallBudgetExceededException("Daily call budget reached.");

        var response = await _client.PostAsync("/api/sync", null);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("title").GetString().Should().Be("Provider call budget reached");
    }

    [Fact]
    public async Task Invalid_webhook_signature_stays_401()
    {
        factory.Failure = new InvalidWebhookSignatureException("bad signature");

        var response = await _client.PostAsync("/api/sync", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sync_guard_exceptions_keep_their_status_codes()
    {
        factory.Failure = new SyncAlreadyRunningException();
        (await _client.PostAsync("/api/sync", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        factory.Failure = new SyncThrottledException(TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
        var throttled = await _client.PostAsync("/api/sync", null);
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().Be(30);
    }

    [Fact]
    public async Task Plain_unexpected_exception_is_still_a_500_without_provider_extensions()
    {
        factory.Failure = new InvalidOperationException("kaboom internal detail");

        var response = await _client.PostAsync("/api/sync", null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("issueKind").And.NotContain("kaboom");
    }

    [Fact]
    public async Task Connections_expose_their_persisted_issue_and_null_when_healthy()
    {
        var at = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var retry = at.AddMinutes(10);
        factory.Connections =
        [
            new ProviderConnectionInfo("Simulated", "Broken Bank", "NeedsUserAction", at, at,
                new ConnectionIssueInfo(nameof(ProviderIssueKind.ReauthRequired), "Login again.", false, true, retry, at, 3)),
            new ProviderConnectionInfo("Simulated", "Fine Bank", "Healthy", at, at),
        ];

        var response = await _client.GetAsync("/api/integrations/connectors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var connections = doc.RootElement.GetProperty("connectors").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "Simulated")
            .GetProperty("connections").EnumerateArray().ToList();
        var broken = connections.Single(c => c.GetProperty("institution").GetString() == "Broken Bank").GetProperty("issue");
        broken.GetProperty("kind").GetString().Should().Be("ReauthRequired");
        broken.GetProperty("message").GetString().Should().Be("Login again.");
        broken.GetProperty("retryable").GetBoolean().Should().BeFalse();
        broken.GetProperty("requiresUserAction").GetBoolean().Should().BeTrue();
        broken.GetProperty("retryAfter").GetDateTimeOffset().Should().Be(retry);
        broken.GetProperty("at").GetDateTimeOffset().Should().Be(at);
        broken.GetProperty("consecutiveFailures").GetInt32().Should().Be(3);
        connections.Single(c => c.GetProperty("institution").GetString() == "Fine Bank")
            .GetProperty("issue").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
