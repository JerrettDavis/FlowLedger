namespace FlowLedger.Integrations.Abstractions;

// ── Base ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Base class for all provider-originated exceptions.
/// Callers should catch specific subclasses rather than this base type unless
/// they intend to handle all provider errors uniformly.
/// </summary>
public abstract class ProviderException : Exception
{
    /// <summary>Provider-specific error code or category, if available.</summary>
    public string? ProviderCode { get; }

    private ProviderIssueKind? _kind;

    /// <summary>
    /// Connector-neutral classification. Defaults from the exception type (see <see cref="DefaultKind"/>); a connector that
    /// knows better (e.g. an institution outage vs. a vendor outage, both "transient") sets it with an object initializer.
    /// </summary>
    public ProviderIssueKind Kind
    {
        get => _kind ?? DefaultKind;
        init => _kind = value;
    }

    /// <summary>The kind implied by the exception type alone.</summary>
    protected virtual ProviderIssueKind DefaultKind => ProviderIssueKind.Unknown;

    protected ProviderException(string message, string? providerCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ProviderCode = providerCode;
    }
}

// ── Taxonomy ──────────────────────────────────────────────────────────────────

/// <summary>
/// A transient connectivity or server error that the caller should retry
/// after a back-off period (e.g. 503, network timeout).
/// </summary>
public sealed class TransientProviderException : ProviderException
{
    /// <summary>Suggested delay before retrying; null if unknown.</summary>
    public TimeSpan? RetryAfter { get; }

    public TransientProviderException(
        string message,
        TimeSpan? retryAfter = null,
        string? providerCode = null,
        Exception? inner = null)
        : base(message, providerCode, inner)
    {
        RetryAfter = retryAfter;
    }

    protected override ProviderIssueKind DefaultKind => ProviderIssueKind.ServiceUnavailable;
}

/// <summary>
/// The caller exceeded the provider's request rate limit.
/// Callers MUST honour <see cref="RetryAfter"/> if provided.
/// </summary>
public sealed class RateLimitedProviderException : ProviderException
{
    /// <summary>Earliest time after which retries are permitted.</summary>
    public DateTimeOffset? RetryAfter { get; }

    public RateLimitedProviderException(
        string message,
        DateTimeOffset? retryAfter = null,
        string? providerCode = null,
        Exception? inner = null)
        : base(message, providerCode, inner)
    {
        RetryAfter = retryAfter;
    }

    protected override ProviderIssueKind DefaultKind => ProviderIssueKind.RateLimited;
}

/// <summary>
/// The provider connection requires the end-user to take action
/// (e.g. re-authenticate, answer MFA, re-link institution).
/// This is not retriable by the system — the user must act.
/// </summary>
public sealed class NeedsUserActionProviderException : ProviderException
{
    /// <summary>Localised hint to surface to the user; may be null.</summary>
    public string? UserHint { get; }

    public NeedsUserActionProviderException(
        string message,
        string? userHint = null,
        string? providerCode = null,
        Exception? inner = null)
        : base(message, providerCode, inner)
    {
        UserHint = userHint;
    }

    protected override ProviderIssueKind DefaultKind => ProviderIssueKind.ReauthRequired;
}

/// <summary>
/// An unrecoverable error (e.g. invalid credentials, unsupported institution).
/// The connection should be marked <see cref="ConnectionStatus.Error"/>;
/// no automatic retry should be attempted.
/// </summary>
public sealed class FatalProviderException : ProviderException
{
    public FatalProviderException(
        string message,
        string? providerCode = null,
        Exception? inner = null)
        : base(message, providerCode, inner) { }

    protected override ProviderIssueKind DefaultKind => ProviderIssueKind.Internal;
}

/// <summary>
/// The webhook signature presented in the request does not match the expected
/// HMAC computed from the shared secret. The request should be rejected (401/403).
/// </summary>
public sealed class InvalidWebhookSignatureException : ProviderException
{
    public InvalidWebhookSignatureException(string message, Exception? inner = null)
        : base(message, providerCode: null, inner) { }
}
