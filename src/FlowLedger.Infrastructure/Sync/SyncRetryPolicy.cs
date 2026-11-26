using FlowLedger.Application.Abstractions;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Infrastructure.Sync;

/// <summary>What an unattended caller (scheduler, webhook job) should do next about a failing connection.</summary>
public enum SyncRetryAction
{
    /// <summary>Try again, but not before <see cref="SyncRetryDecision.RetryAt"/>.</summary>
    Retry,

    /// <summary>Only the user can fix it (re-auth / re-link). Do not retry until they act; skip silently.</summary>
    SkipUntilUserAction,

    /// <summary>Automatic retries are exhausted. Stop and log; a manual sync that succeeds resets this.</summary>
    GiveUp,
}

/// <summary>Who is asking for the sync, which decides how terminal states (user action needed / given up) are treated.</summary>
public enum SyncTrigger
{
    /// <summary>Timer-driven: honours every state, including skipping connections that wait for the user.</summary>
    Scheduled,

    /// <summary>A provider-originated event (webhook): evidence the state may have changed (user reconnected), so terminal states get one attempt. Back-off windows are still honoured.</summary>
    ProviderEvent,
}

public sealed record SyncRetryDecision(SyncRetryAction Action, DateTimeOffset? RetryAt, string Reason);

/// <summary>Whether an unattended sync may run now; when it may not, <see cref="Blocking"/> says why.</summary>
public sealed record SyncAttemptDecision(bool Allowed, SyncRetryDecision? Blocking = null)
{
    public static SyncAttemptDecision Proceed { get; } = new(true);
}

/// <summary>Tunables for <see cref="SyncRetryPolicy"/>. Defaults suit a scheduler that fires every few hours.</summary>
public sealed record SyncRetryOptions
{
    /// <summary>Delay after the first failure; doubles with each further consecutive failure.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Ceiling for the exponential back-off.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Ceiling for a provider-supplied RetryAfter, so a bogus far-future value cannot park a connection for good.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Consecutive failures after which a retryable (outage / throttling) issue stops being retried automatically.</summary>
    public int MaxRetryableFailures { get; init; } = 12;

    /// <summary>Consecutive failures after which an Internal / Unknown / otherwise unclassified issue stops being retried.</summary>
    public int MaxBoundedFailures { get; init; } = 3;
}

/// <summary>
/// The single, connector-agnostic retry/backoff rule (ADR 0018), keyed only on <see cref="ProviderIssue"/> and the persisted
/// consecutive-failure count. Pure apart from the injected clock, so every branch is unit-testable. Scheduled and on-demand
/// jobs consult it (via <see cref="SyncAttemptGate"/>) instead of carrying their own catch blocks.
/// </summary>
public sealed class SyncRetryPolicy
{
    private readonly TimeProvider _time;

    public SyncRetryPolicy(TimeProvider? time = null, SyncRetryOptions? options = null)
    {
        _time = time ?? TimeProvider.System;
        Options = options ?? new SyncRetryOptions();
    }

    public SyncRetryOptions Options { get; }

    /// <summary>Decides what to do about <paramref name="issue"/> given how many consecutive syncs have now failed.</summary>
    /// <param name="failedAt">When the latest failure happened (default: now); back-off is measured from it.</param>
    public SyncRetryDecision Decide(ProviderIssue issue, int consecutiveFailures, DateTimeOffset? failedAt = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var from = failedAt ?? _time.GetUtcNow();
        var failures = Math.Max(1, consecutiveFailures);

        if (issue.Kind == ProviderIssueKind.None)
        {
            return new SyncRetryDecision(SyncRetryAction.Retry, from, "no issue");
        }

        if (issue.RequiresUserAction)
        {
            return new SyncRetryDecision(SyncRetryAction.SkipUntilUserAction, null, $"{issue.Kind}: waiting for the user to act");
        }

        var bounded = !issue.Retryable || issue.Kind is ProviderIssueKind.Internal or ProviderIssueKind.Unknown;
        var limit = bounded ? Options.MaxBoundedFailures : Options.MaxRetryableFailures;
        if (failures >= limit)
        {
            return new SyncRetryDecision(SyncRetryAction.GiveUp, null, $"{issue.Kind}: gave up after {failures} consecutive failures");
        }

        var at = issue.RetryAfter is { } hinted
            ? Min(hinted, from + Options.MaxRetryAfter)
            : from + Backoff(failures);
        return new SyncRetryDecision(SyncRetryAction.Retry, at, $"{issue.Kind}: retry after {at:O}");
    }

    /// <summary>Decides from a persisted issue (its own failure count and timestamp).</summary>
    public SyncRetryDecision Decide(ConnectionIssueInfo issue)
        => Decide(ProviderIssueMapping.ToIssue(issue), issue.ConsecutiveFailures, issue.At);

    /// <summary>May an unattended sync of a connection with this persisted issue (null = healthy) run right now?</summary>
    public SyncAttemptDecision ShouldAttempt(ConnectionIssueInfo? issue, SyncTrigger trigger = SyncTrigger.Scheduled)
    {
        if (issue is null or { Informational: true })
        {
            // Healthy, or an issue seen while reads still succeed: never a reason to hold back unattended syncs.
            return SyncAttemptDecision.Proceed;
        }

        var decision = Decide(issue);
        var due = decision.Action switch
        {
            SyncRetryAction.Retry => _time.GetUtcNow() >= decision.RetryAt,
            _ => trigger == SyncTrigger.ProviderEvent,
        };
        return due ? SyncAttemptDecision.Proceed : new SyncAttemptDecision(false, decision);
    }

    /// <summary>
    /// Tenant-level gate: a tenant with no recorded connections, or with at least one that may be attempted, proceeds;
    /// it is skipped only when every connection is blocked. When blocked, the decision reported is the one that unblocks soonest.
    /// </summary>
    public SyncAttemptDecision ShouldAttempt(IReadOnlyCollection<ProviderConnectionInfo> connections, SyncTrigger trigger = SyncTrigger.Scheduled)
    {
        if (connections.Count == 0)
        {
            return SyncAttemptDecision.Proceed;
        }

        SyncAttemptDecision? soonest = null;
        foreach (var connection in connections)
        {
            var attempt = ShouldAttempt(connection.Issue, trigger);
            if (attempt.Allowed)
            {
                return attempt;
            }

            if (soonest is null || Rank(attempt) < Rank(soonest))
            {
                soonest = attempt;
            }
        }

        return soonest!;
    }

    private TimeSpan Backoff(int failures)
    {
        var factor = Math.Pow(2, Math.Min(failures - 1, 30));
        var seconds = Math.Min(Options.BaseDelay.TotalSeconds * factor, Options.MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    // Retry (soonest first) ranks before the terminal decisions: it is the only one that will unblock by itself.
    private static DateTimeOffset Rank(SyncAttemptDecision d) => d.Blocking is { Action: SyncRetryAction.Retry, RetryAt: { } at } ? at : DateTimeOffset.MaxValue;
}

/// <summary>Converts between the persisted/Application issue shape and the connector-neutral <see cref="ProviderIssue"/>.</summary>
public static class ProviderIssueMapping
{
    public static ConnectionIssueInfo ToInfo(ProviderIssue issue, DateTimeOffset at, int consecutiveFailures = 0, bool informational = false)
        => new(issue.Kind.ToString(), issue.Message, issue.Retryable, issue.RequiresUserAction, issue.RetryAfter, at, consecutiveFailures,
            issue.ProviderCode, issue.RepairRef, informational);

    public static ProviderIssue ToIssue(ConnectionIssueInfo info)
        => new(Enum.TryParse<ProviderIssueKind>(info.Kind, out var kind) ? kind : ProviderIssueKind.Unknown,
            info.Message, info.Retryable, info.RequiresUserAction, info.RetryAfter, info.ProviderCode, info.RepairRef);
}

/// <summary>Reads the tenant's persisted connection health and applies <see cref="SyncRetryPolicy"/>; the one call jobs make before an unattended sync.</summary>
public sealed class SyncAttemptGate(IProviderConnectionStore connections, SyncRetryPolicy policy)
{
    public async Task<SyncAttemptDecision> CheckAsync(SyncTrigger trigger = SyncTrigger.Scheduled, CancellationToken ct = default)
        => policy.ShouldAttempt(await connections.ListAsync(ct), trigger);
}
