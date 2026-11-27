namespace FlowLedger.Api.Endpoints;

/// <summary>
/// Bounded body reading shared by every anonymous webhook endpoint (#94): the size cap is enforced BEFORE any buffering or
/// HMAC work, for both Content-Length and chunked bodies.
/// </summary>
internal static class WebhookBody
{
    /// <summary>Webhook payloads are small JSON events; anything larger is refused before signature work.</summary>
    public const int MaxBytes = ApiLimits.WebhookBodyBytes;

    /// <summary>Reads at most <paramref name="limit"/> bytes; returns null as soon as the body exceeds it.</summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[ApiLimits.BodyReadBufferBytes];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > limit)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }
}
