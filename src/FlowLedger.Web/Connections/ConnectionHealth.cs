using FlowLedger.Web.ApiClient;

namespace FlowLedger.Web.Connections;

/// <summary>What the user can do about a connection problem, independent of which connector produced it.</summary>
public enum ConnectionIssueCategory
{
    /// <summary>No problem.</summary>
    Healthy,
    /// <summary>The user must re-authenticate / fix credentials / re-link.</summary>
    UserAction,
    /// <summary>Temporary; the system retries by itself.</summary>
    Transient,
    /// <summary>Internal or unknown failure; not safe to assume it will fix itself.</summary>
    Fault,
}

public enum HealthTone { Success, Info, Warning, Error }

/// <summary>Presentation model for a connection's state; the badge component only renders this.</summary>
public sealed record ConnectionHealthView(
    ConnectionIssueCategory Category, HealthTone Tone, string Label, string? Detail, DateTimeOffset? RetryAt, bool CanReconnect);

/// <summary>
/// Maps the connector-neutral issue vocabulary (ADR 0018) onto a UI state. Keyed on the issue kind plus the
/// retryable / requiresUserAction flags; never on a connector's native status strings.
/// </summary>
public static class ConnectionHealth
{
    private static readonly HashSet<string> UserActionKinds = new(StringComparer.OrdinalIgnoreCase)
        { "ReauthRequired", "InvalidCredentials", "Disconnected" };

    private static readonly HashSet<string> TransientKinds = new(StringComparer.OrdinalIgnoreCase)
        { "InstitutionUnavailable", "ProviderUnavailable", "ServiceUnavailable", "RateLimited" };

    public const string TransientLabel = "Temporarily unavailable - retrying automatically";

    public static ConnectionIssueCategory Classify(string? kind, bool retryable, bool requiresUserAction)
    {
        if (requiresUserAction || (kind is not null && UserActionKinds.Contains(kind)))
        {
            return ConnectionIssueCategory.UserAction;
        }

        if (retryable || (kind is not null && TransientKinds.Contains(kind)))
        {
            return ConnectionIssueCategory.Transient;
        }

        return string.IsNullOrWhiteSpace(kind) || kind.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? ConnectionIssueCategory.Healthy
            : ConnectionIssueCategory.Fault;
    }

    public static ConnectionHealthView Describe(ConnectionIssueDto? issue, DateTimeOffset now) =>
        issue is null
            ? Describe(null, false, false, null, now)
            : Describe(issue.Kind, issue.Retryable, issue.RequiresUserAction, issue.RetryAfter, now);

    public static ConnectionHealthView Describe(ProblemExtensions? problem, DateTimeOffset now) =>
        problem is null
            ? Describe(null, false, false, null, now)
            : Describe(problem.IssueKind, problem.Retryable, problem.RequiresUserAction,
                problem.RetryAfter is { } span ? now + span : null, now);

    public static ConnectionHealthView Describe(string? kind, bool retryable, bool requiresUserAction, DateTimeOffset? retryAt, DateTimeOffset now)
    {
        switch (Classify(kind, retryable, requiresUserAction))
        {
            case ConnectionIssueCategory.UserAction:
                var (label, detail) = (kind ?? string.Empty).ToLowerInvariant() switch
                {
                    "invalidcredentials" => ("Credentials need updating", "The bank rejected the saved sign-in. Reconnect to enter it again."),
                    "disconnected" => ("Connection removed", "This connection was removed at the provider. Reconnect to link it again."),
                    _ => ("Reconnect required", "The bank needs you to sign in again before syncing can continue."),
                };
                return new(ConnectionIssueCategory.UserAction, HealthTone.Warning, label, detail, null, true);

            case ConnectionIssueCategory.Transient:
                var why = (kind ?? string.Empty).ToLowerInvariant() switch
                {
                    "institutionunavailable" => "The bank is having problems.",
                    "providerunavailable" => "The data provider is having problems.",
                    "serviceunavailable" => "The aggregation service is having problems.",
                    "ratelimited" => "Too many requests; slowing down.",
                    _ => "The provider reported a temporary problem.",
                };
                var text = RetryText(retryAt, now);
                return new(ConnectionIssueCategory.Transient, HealthTone.Info, TransientLabel,
                    text is null ? why : $"{why} Next attempt {text}.", retryAt, false);

            case ConnectionIssueCategory.Fault:
                return new(ConnectionIssueCategory.Fault, HealthTone.Error, "Sync problem",
                    "Something went wrong. Try Sync now; if it keeps failing, contact support.", null, false);

            default:
                return new(ConnectionIssueCategory.Healthy, HealthTone.Success, "Healthy", null, null, false);
        }
    }

    /// <summary>Relative, locale-free wording for a retry time ("in 5 min", "in 2 h"); null when unknown.</summary>
    public static string? RetryText(DateTimeOffset? retryAt, DateTimeOffset now)
    {
        if (retryAt is not { } at)
        {
            return null;
        }

        var span = at - now;
        if (span <= TimeSpan.FromSeconds(45))
        {
            return "shortly";
        }

        if (span < TimeSpan.FromMinutes(90))
        {
            return $"in {Math.Max(1, (int)Math.Round(span.TotalMinutes))} min";
        }

        return span < TimeSpan.FromHours(36)
            ? $"in {(int)Math.Round(span.TotalHours)} h"
            : $"in {(int)Math.Round(span.TotalDays)} days";
    }

    /// <summary>The most urgent issue across connections (user action first, then faults, then transient), or null.</summary>
    public static ConnectionIssueDto? Worst(IEnumerable<ConnectorLinkDto> links) =>
        links.Select(l => l.Issue)
            .Where(i => i is not null && Classify(i.Kind, i.Retryable, i.RequiresUserAction) != ConnectionIssueCategory.Healthy)
            .OrderBy(i => Classify(i!.Kind, i.Retryable, i.RequiresUserAction) switch
            {
                ConnectionIssueCategory.UserAction => 0,
                ConnectionIssueCategory.Fault => 1,
                _ => 2,
            })
            .FirstOrDefault();
}
