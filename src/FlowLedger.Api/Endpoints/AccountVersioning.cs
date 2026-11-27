namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Optimistic concurrency over HTTP for accounts (#157): every account response carries a strong <c>ETag</c> (the row
/// version) and writes honour <c>If-Match</c>. A stale or malformed precondition never overwrites; a missing one (or
/// <c>*</c>) keeps the old last-write-wins behaviour so existing callers are unaffected. Shared mechanics live in
/// <see cref="RowVersioning"/>.
/// </summary>
internal static class AccountVersioning
{
    /// <summary>Reads <c>If-Match</c>; <paramref name="malformed"/> is true when the header is present but not an account ETag.</summary>
    internal static uint? ExpectedVersion(HttpRequest request, out bool malformed) => RowVersioning.ExpectedVersion(request, out malformed);

    internal static IResult Malformed() => RowVersioning.Malformed("account");

    /// <summary>200 with the account and its ETag.</summary>
    internal static IResult Ok(HttpResponse response, AccountDto dto) => RowVersioning.Ok(response, dto, dto.Version);
}
