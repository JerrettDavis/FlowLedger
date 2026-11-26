using FlowLedger.Application.Abstractions;
using FlowLedger.Infrastructure.Sync;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Sync;

/// <summary>Every branch of the connector-agnostic retry policy (ADR 0018). Pure: no database, no Docker.</summary>
public sealed class SyncRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly Clock _clock = new(Now);
    private readonly SyncRetryOptions _opt = new();

    private SyncRetryPolicy Policy() => new(_clock, _opt);

    private static readonly ProviderIssueKind[] RetryableKinds =
    [
        ProviderIssueKind.InstitutionUnavailable, ProviderIssueKind.ProviderUnavailable,
        ProviderIssueKind.ServiceUnavailable, ProviderIssueKind.RateLimited,
    ];

    private static readonly ProviderIssueKind[] UserActionKinds =
        [ProviderIssueKind.ReauthRequired, ProviderIssueKind.InvalidCredentials, ProviderIssueKind.Disconnected];

    [Fact]
    public void User_action_kinds_are_skipped_until_the_user_acts_whatever_the_failure_count()
    {
        foreach (var kind in UserActionKinds)
        {
            foreach (var failures in new[] { 1, 5, 500 })
            {
                var d = Policy().Decide(ProviderIssue.Of(kind, "m"), failures);
                d.Action.Should().Be(SyncRetryAction.SkipUntilUserAction, kind.ToString());
                d.RetryAt.Should().BeNull();
            }
        }
    }

    [Fact]
    public void Retryable_kinds_back_off_exponentially_up_to_the_cap()
    {
        foreach (var kind in RetryableKinds.Where(k => k != ProviderIssueKind.RateLimited))
        {
            var issue = ProviderIssue.Of(kind, "m");
            Policy().Decide(issue, 1).RetryAt.Should().Be(Now + TimeSpan.FromMinutes(15));
            Policy().Decide(issue, 2).RetryAt.Should().Be(Now + TimeSpan.FromMinutes(30));
            Policy().Decide(issue, 3).RetryAt.Should().Be(Now + TimeSpan.FromMinutes(60));
            Policy().Decide(issue, 11).RetryAt.Should().Be(Now + _opt.MaxDelay, "capped");
        }
    }

    [Fact]
    public void Zero_or_negative_failure_counts_are_treated_as_the_first_failure()
        => Policy().Decide(ProviderIssue.Of(ProviderIssueKind.ServiceUnavailable, "m"), 0).RetryAt.Should().Be(Now + _opt.BaseDelay);

    [Fact]
    public void Back_off_is_measured_from_when_the_failure_happened()
    {
        var failedAt = Now.AddHours(-1);
        Policy().Decide(ProviderIssue.Of(ProviderIssueKind.ServiceUnavailable, "m"), 1, failedAt).RetryAt
            .Should().Be(failedAt + _opt.BaseDelay);
    }

    [Fact]
    public void Retry_after_is_honoured_instead_of_exponential_back_off()
    {
        var hinted = Now.AddMinutes(7);
        Policy().Decide(ProviderIssue.Of(ProviderIssueKind.RateLimited, "m", hinted), 4).RetryAt.Should().Be(hinted);
    }

    [Fact]
    public void An_absurd_retry_after_is_capped()
    {
        var d = Policy().Decide(ProviderIssue.Of(ProviderIssueKind.RateLimited, "m", Now.AddYears(5)), 1);
        d.RetryAt.Should().Be(Now + _opt.MaxRetryAfter);
    }

    [Fact]
    public void Retryable_issues_give_up_at_the_failure_threshold()
    {
        var issue = ProviderIssue.Of(ProviderIssueKind.InstitutionUnavailable, "m");
        Policy().Decide(issue, _opt.MaxRetryableFailures - 1).Action.Should().Be(SyncRetryAction.Retry);
        Policy().Decide(issue, _opt.MaxRetryableFailures).Action.Should().Be(SyncRetryAction.GiveUp);
        Policy().Decide(issue, _opt.MaxRetryableFailures + 10).RetryAt.Should().BeNull();
    }

    [Fact]
    public void Internal_and_unknown_get_only_bounded_retries()
    {
        foreach (var kind in new[] { ProviderIssueKind.Internal, ProviderIssueKind.Unknown })
        {
            var issue = ProviderIssue.Of(kind, "m");
            Policy().Decide(issue, 1).Action.Should().Be(SyncRetryAction.Retry, kind.ToString());
            Policy().Decide(issue, _opt.MaxBoundedFailures - 1).Action.Should().Be(SyncRetryAction.Retry, kind.ToString());
            Policy().Decide(issue, _opt.MaxBoundedFailures).Action.Should().Be(SyncRetryAction.GiveUp, kind.ToString());
        }
    }

    [Fact]
    public void An_issue_that_is_neither_retryable_nor_user_actionable_is_bounded_too()
    {
        var odd = new ProviderIssue(ProviderIssueKind.ServiceUnavailable, "m", Retryable: false, RequiresUserAction: false);
        Policy().Decide(odd, _opt.MaxBoundedFailures).Action.Should().Be(SyncRetryAction.GiveUp);
    }

    [Fact]
    public void Every_kind_yields_a_decision()
    {
        foreach (var kind in Enum.GetValues<ProviderIssueKind>())
        {
            var act = () => Policy().Decide(ProviderIssue.Of(kind, "m"), 1);
            act.Should().NotThrow(kind.ToString());
        }
    }

    // ── Gate: persisted issue + clock ─────────────────────────────────────────

    private static ConnectionIssueInfo Persisted(ProviderIssueKind kind, int failures, DateTimeOffset at, DateTimeOffset? retryAfter = null)
        => ProviderIssueMapping.ToInfo(ProviderIssue.Of(kind, "m", retryAfter), at, failures);

    [Fact]
    public void Healthy_connections_always_proceed()
        => Policy().ShouldAttempt((ConnectionIssueInfo?)null).Allowed.Should().BeTrue();

    [Fact]
    public void A_retryable_issue_blocks_until_its_back_off_window_has_passed()
    {
        var issue = Persisted(ProviderIssueKind.ServiceUnavailable, 1, Now);

        Policy().ShouldAttempt(issue).Allowed.Should().BeFalse();
        _clock.Advance(_opt.BaseDelay - TimeSpan.FromSeconds(1));
        Policy().ShouldAttempt(issue).Allowed.Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));
        Policy().ShouldAttempt(issue).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_rate_limit_blocks_until_retry_after()
    {
        var issue = Persisted(ProviderIssueKind.RateLimited, 1, Now, Now.AddMinutes(3));

        Policy().ShouldAttempt(issue).Blocking!.RetryAt.Should().Be(Now.AddMinutes(3));
        _clock.Advance(TimeSpan.FromMinutes(3));
        Policy().ShouldAttempt(issue).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Scheduled_triggers_never_retry_user_action_or_given_up_connections()
    {
        _clock.Advance(TimeSpan.FromDays(30));
        Policy().ShouldAttempt(Persisted(ProviderIssueKind.ReauthRequired, 1, Now)).Allowed.Should().BeFalse();
        Policy().ShouldAttempt(Persisted(ProviderIssueKind.Internal, 99, Now)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_provider_event_gives_terminal_states_one_attempt_but_still_honours_back_off()
    {
        Policy().ShouldAttempt(Persisted(ProviderIssueKind.ReauthRequired, 1, Now), SyncTrigger.ProviderEvent).Allowed.Should().BeTrue();
        Policy().ShouldAttempt(Persisted(ProviderIssueKind.Internal, 99, Now), SyncTrigger.ProviderEvent).Allowed.Should().BeTrue();
        Policy().ShouldAttempt(Persisted(ProviderIssueKind.ServiceUnavailable, 1, Now), SyncTrigger.ProviderEvent).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_tenant_is_skipped_only_when_every_connection_is_blocked()
    {
        ProviderConnectionInfo Conn(string name, ConnectionIssueInfo? issue) => new(name, "Bank", "Active", Now, Now, issue);
        var blockedA = Conn("a", Persisted(ProviderIssueKind.ReauthRequired, 1, Now));
        var blockedB = Conn("b", Persisted(ProviderIssueKind.ServiceUnavailable, 2, Now));
        var healthy = Conn("c", null);

        Policy().ShouldAttempt([]).Allowed.Should().BeTrue("no connections recorded: nothing to gate");
        Policy().ShouldAttempt([blockedA, blockedB]).Allowed.Should().BeFalse();
        Policy().ShouldAttempt([blockedA, blockedB, healthy]).Allowed.Should().BeTrue();

        // The reported reason is the one that will unblock by itself (the retry), not the terminal one.
        Policy().ShouldAttempt([blockedA, blockedB]).Blocking!.Action.Should().Be(SyncRetryAction.Retry);
    }

    [Fact]
    public void Unrecognised_persisted_kinds_are_treated_as_unknown()
    {
        var info = new ConnectionIssueInfo("FromTheFuture", "m", true, false, null, Now, 1);
        ProviderIssueMapping.ToIssue(info).Kind.Should().Be(ProviderIssueKind.Unknown);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
