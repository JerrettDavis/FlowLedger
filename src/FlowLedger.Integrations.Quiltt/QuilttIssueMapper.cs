using System.Net;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Quiltt;

/// <summary>
/// The ONE place Quiltt-native failure vocabulary (Connection statuses, HTTP/GraphQL error shapes) becomes the connector-neutral
/// <see cref="ProviderIssue"/> / <see cref="ProviderException"/> vocabulary. Nothing outside the Quiltt project sees these strings.
/// The mapping table and the sandbox evidence behind it live in docs/quiltt-sandbox-scenarios.md.
/// </summary>
internal static class QuilttIssueMapper
{
    // ── Connection status → issue ────────────────────────────────────────────

    /// <summary>
    /// Maps a Quiltt <c>ConnectionStatus</c> to an issue. <c>SYNCED</c>, <c>SYNCING</c>, <c>INITIALIZING</c> and <c>UPGRADING</c>
    /// are not problems. <paramref name="connectionId"/> rides in <see cref="ProviderIssue.ProviderCode"/> (see
    /// <see cref="QuilttProviderCode"/>) so a UI can open the Connector in reconnect mode for exactly that connection.
    /// </summary>
    public static ProviderIssue FromConnectionStatus(string? status, string? connectionId)
    {
        var s = (status ?? string.Empty).Trim().ToUpperInvariant();
        var code = QuilttProviderCode.Format(s, connectionId);
        var issue = s switch
        {
            "SYNCED" or "SYNCING" or "INITIALIZING" or "UPGRADING" => ProviderIssue.NoIssue,
            "ERROR_REPAIRABLE" => ProviderIssue.Of(ProviderIssueKind.ReauthRequired,
                "The bank connection must be re-authenticated before syncing can resume.", providerCode: code),
            "ERROR_INSTITUTION" => ProviderIssue.Of(ProviderIssueKind.InstitutionUnavailable,
                "The bank is reporting an error. Syncing is retried automatically.", providerCode: code),
            "ERROR_PROVIDER" => ProviderIssue.Of(ProviderIssueKind.ProviderUnavailable,
                "The upstream data aggregator is reporting an error. Syncing is retried automatically.", providerCode: code),
            "ERROR_SERVICE" => ProviderIssue.Of(ProviderIssueKind.ServiceUnavailable,
                "Quiltt is experiencing an internal error. Syncing is retried automatically.", providerCode: code),
            "DISCONNECTING" or "DISCONNECTED" => ProviderIssue.Of(ProviderIssueKind.Disconnected,
                "The bank connection was disconnected. Link the bank again.", providerCode: code),
            _ => ProviderIssue.Of(ProviderIssueKind.Unknown, "Quiltt reported an unrecognised connection status.", providerCode: code),
        };

        // The connection id is what the Connector's reconnect mode needs; it travels as the opaque RepairRef.
        return issue.Kind == ProviderIssueKind.None || string.IsNullOrWhiteSpace(connectionId) ? issue : issue with { RepairRef = connectionId };
    }

    /// <summary>Which of two problems the user should hear about first: things only they can fix, then the rest by severity.</summary>
    internal static int Severity(ProviderIssueKind kind) => kind switch
    {
        ProviderIssueKind.ReauthRequired => 100,
        ProviderIssueKind.InvalidCredentials => 95,
        ProviderIssueKind.Disconnected => 90,
        ProviderIssueKind.Internal => 70,
        ProviderIssueKind.ServiceUnavailable => 60,
        ProviderIssueKind.ProviderUnavailable => 50,
        ProviderIssueKind.InstitutionUnavailable => 40,
        ProviderIssueKind.RateLimited => 30,
        ProviderIssueKind.Unknown => 20,
        _ => 0,
    };

    /// <summary>
    /// Folds every connection of a profile into one health. Rules: no connections = pending; the worst issue wins; a DISCONNECTED
    /// connection only counts when it is the profile's only kind of connection (Quiltt keeps them forever, so they must not
    /// flag a profile that has since been re-linked); otherwise any SYNCING/INITIALIZING connection means Syncing.
    /// </summary>
    public static ConnectionHealth Aggregate(IReadOnlyCollection<(string? Id, string? Status)> connections)
    {
        if (connections.Count == 0)
        {
            return new ConnectionHealth(ConnectionStatus.ConnectionPending);
        }

        var live = connections.Where(c => !IsDisconnected(c.Status)).ToList();
        var considered = live.Count > 0 ? live : [.. connections];

        var worst = considered
            .Select(c => FromConnectionStatus(c.Status, c.Id))
            .OrderByDescending(i => Severity(i.Kind))
            .First();
        if (worst.Kind != ProviderIssueKind.None)
        {
            return ConnectionHealth.FromIssue(worst);
        }

        var busy = considered.Any(c => (c.Status ?? string.Empty).ToUpperInvariant() is "SYNCING" or "INITIALIZING" or "UPGRADING");
        return busy ? new ConnectionHealth(ConnectionStatus.Syncing, ProviderIssue.NoIssue) : ConnectionHealth.Healthy;
    }

    /// <summary>
    /// For reads: only throw when NO connection is usable. A mix of healthy and broken connections still syncs the healthy data;
    /// the broken one is reported through <c>GetConnectionHealthAsync</c>. Returns null when reads may proceed.
    /// </summary>
    public static ProviderException? BlockingException(IReadOnlyCollection<(string? Id, string? Status)> connections)
    {
        if (connections.Count == 0)
        {
            return null;
        }

        var issues = connections.Select(c => (c, Issue: FromConnectionStatus(c.Status, c.Id))).ToList();
        if (issues.Any(x => x.Issue.Kind == ProviderIssueKind.None))
        {
            return null;
        }

        var live = issues.Where(x => !IsDisconnected(x.c.Status)).ToList();
        var worst = (live.Count > 0 ? live : issues).OrderByDescending(x => Severity(x.Issue.Kind)).First().Issue;
        return ToException(worst);
    }

    /// <summary>The typed exception that carries <paramref name="issue"/> (Kind refined, retry/user-action implied by type).</summary>
    public static ProviderException ToException(ProviderIssue issue) => issue.Kind switch
    {
        ProviderIssueKind.ReauthRequired or ProviderIssueKind.InvalidCredentials or ProviderIssueKind.Disconnected =>
            new NeedsUserActionProviderException(issue.Message, "Reconnect the bank.", issue.ProviderCode) { Kind = issue.Kind, RepairRef = issue.RepairRef },
        ProviderIssueKind.RateLimited => new RateLimitedProviderException(issue.Message, issue.RetryAfter, issue.ProviderCode) { RepairRef = issue.RepairRef },
        ProviderIssueKind.Internal or ProviderIssueKind.Unknown => new FatalProviderException(issue.Message, issue.ProviderCode) { Kind = issue.Kind, RepairRef = issue.RepairRef },
        _ => new TransientProviderException(issue.Message, providerCode: issue.ProviderCode) { Kind = issue.Kind, RepairRef = issue.RepairRef },
    };

    private static bool IsDisconnected(string? status) =>
        (status ?? string.Empty).Trim().ToUpperInvariant() is "DISCONNECTED" or "DISCONNECTING";

    // ── HTTP / GraphQL failures → exceptions ─────────────────────────────────

    /// <summary>The Session API (<c>POST /users/sessions</c>, server key) vs the GraphQL API (profile session token).</summary>
    public enum Endpoint { Session, GraphQl }

    /// <summary>
    /// Verified wire shapes (sandbox): HTTP 401 <c>{"message":"Not Authenticated","instruction":...,"requestId":...}</c> for a bad API key
    /// (session) or a bad/expired session token (graphql); HTTP 429 <c>{"message":"Too Many Requests",...}</c> with NO Retry-After
    /// header when more than the per-profile hourly session quota is minted. Bodies are never copied into messages.
    /// </summary>
    public static ProviderException FromHttpFailure(HttpStatusCode status, Endpoint endpoint, TimeSpan? retryAfter, TimeSpan rateLimitFallback, TimeProvider time)
    {
        var code = (int)status;
        var what = endpoint == Endpoint.Session ? "session" : "graphql";
        switch (status)
        {
            case HttpStatusCode.Unauthorized when endpoint == Endpoint.Session:
            case HttpStatusCode.Forbidden when endpoint == Endpoint.Session:
                // Operator problem (wrong/revoked server key), not something the end user can repair.
                return new FatalProviderException($"Quiltt rejected the server API key (HTTP {code}).", $"HTTP_{code}") { Kind = ProviderIssueKind.Internal };
            case HttpStatusCode.Unauthorized:
                // A freshly minted session token was refused (expired mid-sync, clock skew): a new sync mints a new one.
                return new TransientProviderException($"Quiltt rejected the profile session (HTTP {code}).", providerCode: $"HTTP_{code}") { Kind = ProviderIssueKind.Internal };
            case HttpStatusCode.Forbidden:
                // Documented: per-connection calls on an ERROR_REPAIRABLE connection return 403 Forbidden.
                return new NeedsUserActionProviderException(
                    $"Quiltt refused the request (HTTP {code}); the bank connection likely needs to be re-authenticated.", "Reconnect the bank.", $"HTTP_{code}")
                { Kind = ProviderIssueKind.ReauthRequired };
            case HttpStatusCode.TooManyRequests:
                var delay = retryAfter ?? rateLimitFallback;
                return new RateLimitedProviderException($"Quiltt {what} rate limit reached.", time.GetUtcNow() + delay, "HTTP_429");
        }

        if (status == HttpStatusCode.InternalServerError)
        {
            return new TransientProviderException($"Quiltt {what} request failed (HTTP {code}).", providerCode: $"HTTP_{code}") { Kind = ProviderIssueKind.Internal };
        }

        if (code >= 500 || status == HttpStatusCode.RequestTimeout)
        {
            return new TransientProviderException($"Quiltt {what} request failed (HTTP {code}).", providerCode: $"HTTP_{code}") { Kind = ProviderIssueKind.ServiceUnavailable };
        }

        return new FatalProviderException($"Quiltt {what} request failed (HTTP {code}).", $"HTTP_{code}") { Kind = ProviderIssueKind.Internal };
    }

    /// <summary>A connection-level failure before any HTTP status (DNS, TLS, reset, timeout) is a Quiltt-service outage as far as we can tell.</summary>
    public static ProviderException FromTransportFailure(Exception ex) =>
        new TransientProviderException("Quiltt could not be reached.", providerCode: "TRANSPORT", inner: ex) { Kind = ProviderIssueKind.ServiceUnavailable };

    /// <summary>
    /// GraphQL returns HTTP 200 with <c>errors[]</c>; <c>extensions.code</c> carries the machine code (validation failures are
    /// camelCase, e.g. <c>undefinedField</c> / <c>argumentNotAccepted</c>, and mean OUR query is wrong, never the user's bank).
    /// </summary>
    public static ProviderException FromGraphQlErrors(IReadOnlyList<QGraphQlError> errors, TimeSpan rateLimitFallback, TimeProvider time)
    {
        var codes = errors.Select(e => ErrorCode(e)).Where(c => c.Length > 0).Distinct().ToList();
        var joined = codes.Count > 0 ? string.Join(",", codes) : null;
        var haystack = string.Join(' ', codes.Concat(errors.Select(e => e.Message ?? string.Empty))).ToUpperInvariant();

        if (haystack.Contains("RATE_LIMIT") || haystack.Contains("RATELIMIT") || haystack.Contains("TOO MANY"))
        {
            return new RateLimitedProviderException("Quiltt GraphQL rate limit reached.", time.GetUtcNow() + rateLimitFallback, joined);
        }

        if (haystack.Contains("FORBIDDEN") || haystack.Contains("REPAIRABLE") || haystack.Contains("REAUTH"))
        {
            return new NeedsUserActionProviderException("Quiltt refused the query; the bank connection needs to be re-authenticated.",
                "Reconnect the bank.", joined)
            { Kind = ProviderIssueKind.ReauthRequired };
        }

        if (haystack.Contains("NOT AUTHENTICATED") || haystack.Contains("UNAUTHENTICATED") || haystack.Contains("UNAUTHORIZED") || haystack.Contains("EXPIRED"))
        {
            return new TransientProviderException("Quiltt rejected the profile session.", providerCode: joined) { Kind = ProviderIssueKind.Internal };
        }

        if (haystack.Contains("TIMEOUT") || haystack.Contains("UNAVAILABLE") || haystack.Contains("INTERNAL_SERVER") || haystack.Contains("SERVICE"))
        {
            return new TransientProviderException("Quiltt reported a service error.", providerCode: joined) { Kind = ProviderIssueKind.ServiceUnavailable };
        }

        // Everything else (schema/validation errors included) is a defect on our side: not retryable by the user, surfaced as Internal.
        return new FatalProviderException("Quiltt GraphQL query failed.", joined) { Kind = ProviderIssueKind.Internal };
    }

    private static string ErrorCode(QGraphQlError e) =>
        e.Extensions is not null && e.Extensions.TryGetValue("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            ? c.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// The Quiltt-specific payload of <see cref="ProviderIssue.ProviderCode"/>: <c>"&lt;STATUS&gt;:&lt;connectionId&gt;"</c> (for example
/// <c>ERROR_REPAIRABLE:conn_14TJ...</c>). Public so the host can read the connection id to launch the Connector in reconnect mode
/// without knowing anything else about Quiltt's status strings.
/// </summary>
internal static class QuilttProviderCode
{
    public static string? Format(string? status, string? connectionId) =>
        string.IsNullOrWhiteSpace(status) ? null : string.IsNullOrWhiteSpace(connectionId) ? status : $"{status}:{connectionId}";

    /// <summary>Extracts the Quiltt connection id (<c>conn_...</c>) from an issue's provider code; null when absent.</summary>
    public static string? TryGetConnectionId(string? providerCode)
    {
        if (string.IsNullOrWhiteSpace(providerCode))
        {
            return null;
        }

        var i = providerCode.IndexOf(':', StringComparison.Ordinal);
        return i >= 0 && i < providerCode.Length - 1 ? providerCode[(i + 1)..] : null;
    }
}
