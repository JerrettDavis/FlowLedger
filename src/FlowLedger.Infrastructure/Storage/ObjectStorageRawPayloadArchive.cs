using System.Security.Cryptography;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Storage;

/// <summary>
/// <see cref="IRawPayloadArchive"/> backed by <see cref="IObjectStorage"/>. Keys are
/// <c>raw/{provider}/{tenant|_unattributed}/{yyyy}/{MM}/{dd}/{utcTicks}-{operation}-{sha256[0..16]}.json</c>:
/// partitioned by tenant (#142, so one tenant's payloads can be purged or exported alone), time-ordered (replayable in order),
/// and content-hashed (distinguishes same-instant writes). Objects written before the tenant folder existed keep their old key
/// and are read exactly as before. With <c>Storage:EncryptionKey</c> set, new objects are sealed (see <see cref="RawArchiveSettings"/>).
/// Nothing in this class ever deletes or overwrites an object.
/// </summary>
internal sealed class ObjectStorageRawPayloadArchive(IObjectStorage storage, RawArchiveSettings? settings = null) : IRawPayloadArchive
{
    private readonly RawArchiveSettings _settings = settings ?? RawArchiveSettings.Default;

    public async Task<string> ArchiveAsync(
        string provider, string operation, ReadOnlyMemory<byte> payload,
        RawPayloadContext? context = null, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = context?.TenantId;
        var tenantSegment = tenantId is { } t && t != Guid.Empty ? t.ToString("N") : RawArchiveSettings.UnattributedSegment;
        var hash = Convert.ToHexString(SHA256.HashData(payload.Span))[..16].ToLowerInvariant();
        var key = $"raw/{Sanitize(provider)}/{tenantSegment}/{now:yyyy}/{now:MM}/{now:dd}/{now.UtcTicks:D19}-{Sanitize(operation)}-{hash}.json";

        var content = payload.ToArray();
        if (_settings.Encrypts)
        {
            content = System.Text.Encoding.ASCII.GetBytes(
                IRawPayloadDecryptor.Magic + SecretBox.SealV2(content, _settings.Ring, RawArchiveMaintenance.Aad(tenantId, provider)));
        }

        using var stream = new MemoryStream(content, writable: false);
        await storage.UploadAsync(key, stream, "application/json", cancellationToken).ConfigureAwait(false);

        if (context is not null)
        {
            // The sidecar holds request metadata only (method, path, status, byte count), never the payload, so it stays readable.
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
                tenant = tenantSegment,
                encrypted = _settings.Encrypts,
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
