using System.Globalization;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Optimistic concurrency over HTTP (#157): a read carries a strong <c>ETag</c> (the row version) and writes honour
/// <c>If-Match</c>. A stale precondition never overwrites (409 ProblemDetails); a malformed one is a 400; a missing one (or
/// <c>*</c>) keeps last-write-wins so existing callers are unaffected. Accounts, transactions, recurring flows and categories share it.
/// </summary>
internal static class RowVersioning
{
    /// <summary>Reads <c>If-Match</c>; <paramref name="malformed"/> is true when the header is present but not a row ETag.</summary>
    internal static uint? ExpectedVersion(HttpRequest request, out bool malformed)
    {
        malformed = false;
        var raw = request.Headers.IfMatch.ToString().Trim();
        if (raw.Length == 0 || raw == "*")
        {
            return null;
        }

        var token = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
        token = token.Trim('"');
        if (uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return version;
        }

        malformed = true;
        return null;
    }

    internal static IResult Malformed(string noun) =>
        Results.Problem($"If-Match must be the ETag of the {noun} as last read.", statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Puts the transaction's current ETag on the response, so a client that just changed it can chain its next write without re-reading.</summary>
    internal static async Task StampTransactionAsync(HttpResponse response, FlowLedger.Application.Features.Transactions.GetTransactionHandler reader, Guid id, CancellationToken ct)
    {
        if (await reader.HandleAsync(id, ct) is { } current)
        {
            response.Headers.ETag = $"\"{current.Version}\"";
        }
    }

    /// <summary>200 with the body and its ETag.</summary>
    internal static IResult Ok<T>(HttpResponse response, T body, uint version)
    {
        response.Headers.ETag = $"\"{version}\"";
        return Results.Ok(body);
    }
}
