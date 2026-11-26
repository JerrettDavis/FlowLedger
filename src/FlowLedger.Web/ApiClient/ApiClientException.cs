using System.Net;

namespace FlowLedger.Web.ApiClient;

/// <summary>
/// Thrown by <see cref="FlowLedgerApiClient"/> when an HTTP call fails or the
/// response cannot be deserialized.  Always carries a user-safe message that
/// callers can display directly in the UI; the original cause is preserved as
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class ApiClientException : Exception
{
    /// <summary>HTTP status code from the response, when one was received.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Connector-neutral issue extensions from the API's ProblemDetails, when the response carried them.</summary>
    public ProblemExtensions? Problem { get; }

    public string? IssueKind => Problem?.IssueKind;
    public bool Retryable => Problem?.Retryable ?? false;
    public bool RequiresUserAction => Problem?.RequiresUserAction ?? false;
    public int? RetryAfterSeconds => Problem?.RetryAfterSeconds;
    public string? Provider => Problem?.Provider;

    public ApiClientException(string userMessage, HttpStatusCode? statusCode = null, Exception? inner = null, ProblemExtensions? problem = null)
        : base(userMessage, inner)
    {
        StatusCode = statusCode;
        Problem = problem;
    }
}
