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

    /// <summary>Per-field validation messages (field name to first message) from a 400 problem's <c>errors</c> map, when present.</summary>
    public IReadOnlyDictionary<string, string>? FieldErrors { get; }

    /// <summary>The validation message for <paramref name="field"/> (case-insensitive), or null.</summary>
    public string? FieldError(string field) => FieldErrors is not null && FieldErrors.TryGetValue(field, out var m) ? m : null;

    public string? IssueKind => Problem?.IssueKind;
    public bool Retryable => Problem?.Retryable ?? false;
    public bool RequiresUserAction => Problem?.RequiresUserAction ?? false;
    public int? RetryAfterSeconds => Problem?.RetryAfterSeconds;
    public string? Provider => Problem?.Provider;

    public ApiClientException(string userMessage, HttpStatusCode? statusCode = null, Exception? inner = null, ProblemExtensions? problem = null,
        IReadOnlyDictionary<string, string>? fieldErrors = null)
        : base(userMessage, inner)
    {
        FieldErrors = fieldErrors;
        StatusCode = statusCode;
        Problem = problem;
    }
}
