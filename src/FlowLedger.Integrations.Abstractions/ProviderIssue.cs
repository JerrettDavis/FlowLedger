namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// Connector-neutral classification of what is wrong with a provider connection. Every connector maps its own native
/// statuses and errors onto this one vocabulary, so persistence, retry policy, HTTP mapping and the UI are written once.
/// </summary>
public enum ProviderIssueKind
{
    /// <summary>Nothing is wrong.</summary>
    None = 0,

    /// <summary>The institution wants the user to log in again, answer MFA or re-consent. User action required.</summary>
    ReauthRequired,

    /// <summary>The credentials were rejected. User action required (re-enter or re-link).</summary>
    InvalidCredentials,

    /// <summary>The bank or credit union is down or erroring. Not the user's fault; retry later.</summary>
    InstitutionUnavailable,

    /// <summary>The upstream aggregator network (Plaid/MX/Finicity/...) is down or erroring. Retry later.</summary>
    ProviderUnavailable,

    /// <summary>The connector vendor's own service (Quiltt/SimpleFIN/...) is down or erroring. Retry later.</summary>
    ServiceUnavailable,

    /// <summary>We are being throttled. Honour <see cref="ProviderIssue.RetryAfter"/>.</summary>
    RateLimited,

    /// <summary>The connection was disconnected or removed at the provider. User must re-link.</summary>
    Disconnected,

    /// <summary>An internal/unexpected failure at the provider that retrying is unlikely to fix quickly.</summary>
    Internal,

    /// <summary>The error could not be classified.</summary>
    Unknown,
}

/// <summary>A classified problem with a connection, with everything a caller needs to decide what to do next.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Message">Operator-facing detail. Never contains secrets or bank identifiers.</param>
/// <param name="Retryable">True when a later automatic retry may succeed without anyone doing anything.</param>
/// <param name="RequiresUserAction">True when only the user can fix it (re-auth / re-link).</param>
/// <param name="RetryAfter">Earliest sensible retry time, when the provider said so.</param>
/// <param name="ProviderCode">The provider's own code/status string, for diagnostics.</param>
/// <param name="RepairRef">Opaque, connector-defined reference a UI hands back to that connector's repair flow (for example the id of
/// the exact connection to reconnect). Never interpreted outside the connector that produced it.</param>
public sealed record ProviderIssue(
    ProviderIssueKind Kind,
    string Message,
    bool Retryable,
    bool RequiresUserAction,
    DateTimeOffset? RetryAfter = null,
    string? ProviderCode = null,
    string? RepairRef = null)
{
    /// <summary>The "all good" issue.</summary>
    public static ProviderIssue NoIssue { get; } = new(ProviderIssueKind.None, string.Empty, false, false);

    /// <summary>Builds an issue of <paramref name="kind"/> with the retry/user-action defaults that kind implies.</summary>
    public static ProviderIssue Of(ProviderIssueKind kind, string message, DateTimeOffset? retryAfter = null, string? providerCode = null, string? repairRef = null) =>
        new(kind, message, IsRetryableByDefault(kind), RequiresUserByDefault(kind), retryAfter, providerCode, repairRef);

    public static bool IsRetryableByDefault(ProviderIssueKind kind) => kind is
        ProviderIssueKind.InstitutionUnavailable or ProviderIssueKind.ProviderUnavailable
        or ProviderIssueKind.ServiceUnavailable or ProviderIssueKind.RateLimited or ProviderIssueKind.Internal;

    public static bool RequiresUserByDefault(ProviderIssueKind kind) => kind is
        ProviderIssueKind.ReauthRequired or ProviderIssueKind.InvalidCredentials or ProviderIssueKind.Disconnected;

    /// <summary>The coarse lifecycle status this issue puts a connection in.</summary>
    public ConnectionStatus ToConnectionStatus() => Kind switch
    {
        ProviderIssueKind.None => ConnectionStatus.Healthy,
        ProviderIssueKind.ReauthRequired or ProviderIssueKind.InvalidCredentials or ProviderIssueKind.Disconnected => ConnectionStatus.NeedsUserAction,
        ProviderIssueKind.InstitutionUnavailable or ProviderIssueKind.ProviderUnavailable
            or ProviderIssueKind.ServiceUnavailable or ProviderIssueKind.RateLimited => ConnectionStatus.Degraded,
        _ => ConnectionStatus.Error,
    };

    /// <summary>Classifies any exception. Provider exceptions carry their own kind; everything else is <see cref="ProviderIssueKind.Unknown"/>.</summary>
    public static ProviderIssue FromException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is not ProviderException pe)
        {
            return Of(ProviderIssueKind.Unknown, ex.GetType().Name);
        }

        var retryAfter = pe switch
        {
            RateLimitedProviderException r => r.RetryAfter,
            TransientProviderException { RetryAfter: { } t } => DateTimeOffset.UtcNow + t,
            _ => (DateTimeOffset?)null,
        };
        return Of(pe.Kind, pe.Message, retryAfter, pe.ProviderCode, pe.RepairRef);
    }
}

/// <summary>The current state of one connection: its lifecycle status plus the classified issue (if any).</summary>
public sealed record ConnectionHealth(ConnectionStatus Status, ProviderIssue? Issue = null)
{
    public static ConnectionHealth Healthy { get; } = new(ConnectionStatus.Healthy, ProviderIssue.NoIssue);

    /// <summary>Health derived from an issue (status follows <see cref="ProviderIssue.ToConnectionStatus"/>).</summary>
    public static ConnectionHealth FromIssue(ProviderIssue issue) => new(issue.ToConnectionStatus(), issue);

    public bool IsHealthy => Issue is null or { Kind: ProviderIssueKind.None };
}
