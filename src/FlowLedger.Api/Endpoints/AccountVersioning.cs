using System.Globalization;
using FlowLedger.Application.Features.Accounts;
using Microsoft.AspNetCore.Http;

namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Optimistic concurrency over HTTP for accounts (#157): every account response carries a strong <c>ETag</c> (the row
/// version) and writes honour <c>If-Match</c>. A stale or malformed precondition never overwrites; a missing one (or
/// <c>*</c>) keeps the old last-write-wins behaviour so existing callers are unaffected.
/// </summary>
internal static class AccountVersioning
{
    /// <summary>Reads <c>If-Match</c>; <paramref name="malformed"/> is true when the header is present but not an account ETag.</summary>
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

    internal static IResult Malformed() =>
        Results.Problem("If-Match must be the ETag of the account as last read.", statusCode: StatusCodes.Status400BadRequest);

    /// <summary>200 with the account and its ETag.</summary>
    internal static IResult Ok(HttpResponse response, AccountDto dto)
    {
        response.Headers.ETag = $"\"{dto.Version}\"";
        return Results.Ok(dto);
    }
}
