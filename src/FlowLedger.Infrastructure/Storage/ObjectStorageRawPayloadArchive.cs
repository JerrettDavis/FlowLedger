using System.Security.Cryptography;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Storage;

/// <summary>
/// <see cref="IRawPayloadArchive"/> backed by <see cref="IObjectStorage"/>. Keys are
/// <c>raw/{provider}/{yyyy}/{MM}/{dd}/{utcTicks}-{operation}-{sha256[0..16]}.json</c>:
/// time-ordered (replayable in order), and content-hashed (distinguishes same-instant writes).
/// Nothing in this class ever deletes or overwrites an object.
/// </summary>
internal sealed class ObjectStorageRawPayloadArchive(IObjectStorage storage) : IRawPayloadArchive
{
    public async Task<string> ArchiveAsync(
        string provider, string operation, ReadOnlyMemory<byte> payload,
        RawPayloadContext? context = null, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexString(SHA256.HashData(payload.Span))[..16].ToLowerInvariant();
        var key = $"raw/{Sanitize(provider)}/{now:yyyy}/{now:MM}/{now:dd}/{now.UtcTicks:D19}-{Sanitize(operation)}-{hash}.json";

        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        await storage.UploadAsync(key, stream, "application/json", cancellationToken).ConfigureAwait(false);

        if (context is not null)
        {
            var meta = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                provider,
                operation,
                capturedAtUtc = now,
                sha256Prefix = hash,
                bytes = payload.Length,
                method = context.Method,
                pathAndQuery = context.PathAndQuery,
                statusCode = context.StatusCode,
            });
            using var metaStream = new MemoryStream(meta, writable: false);
            await storage.UploadAsync(key[..^".json".Length] + ".meta.json", metaStream, "application/json", cancellationToken)
                .ConfigureAwait(false);
        }

        return key;
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
