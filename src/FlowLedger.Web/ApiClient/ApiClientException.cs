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

    public ApiClientException(string userMessage, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(userMessage, inner)
    {
        StatusCode = statusCode;
    }
}
