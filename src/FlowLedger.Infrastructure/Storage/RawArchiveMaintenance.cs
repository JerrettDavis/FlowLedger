using FlowLedger.Application.Features.Users;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Storage;

/// <summary>Counts from one <see cref="RawArchiveMaintenance.SealPlaintextAsync"/> run.</summary>
public sealed record SealResult(int Sealed, int AlreadySealed, int Skipped, int Recovered);

/// <summary>
/// Everything the raw payload archive needs besides writing (#142): reading sealed objects, deleting one tenant's objects when the
/// tenant is offboarded (#189), and the opt-in age-based retention sweep. Keys are only ever taken from the storage listing and are
/// re-validated by the storage itself, so no caller-supplied path reaches the file system.
/// </summary>
public sealed class RawArchiveMaintenance : IRawPayloadDecryptor, ITenantFileEraser
{
    private const string RawPrefix = "raw";

    private readonly IObjectStorage storage;
    private readonly IObjectStorageBrowser browser;
    private readonly RawArchiveSettings settings;

    internal RawArchiveMaintenance(IObjectStorage storage, IObjectStorageBrowser browser, RawArchiveSettings settings)
    {
        this.storage = storage;
        this.browser = browser;
        this.settings = settings;
    }

    /// <summary>The additional authenticated data for an archive object: its tenant (empty when unattributed) and provider.</summary>
    public static byte[] Aad(Guid? tenantId, string provider) => SecretBox.Aad(tenantId ?? Guid.Empty, "raw-archive:" + provider);

    public byte[]? TryDecrypt(string provider, string tenantSegment, byte[] content)
    {
        if (!IRawPayloadDecryptor.IsEncrypted(content))
        {
            return content;
        }

        var tenant = Guid.TryParseExact(tenantSegment, "N", out var parsed) ? parsed : (Guid?)null;
        var envelope = System.Text.Encoding.ASCII.GetString(content, IRawPayloadDecryptor.Magic.Length, content.Length - IRawPayloadDecryptor.Magic.Length);
        return SecretBox.OpenV2Bytes(envelope, settings.Ring, Aad(tenant, provider));
    }

    /// <summary>Opens one archive object by key (the storage key as returned by <c>ArchiveAsync</c>), decrypting transparently.</summary>
    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        var segments = key.Split('/');
        if (segments.Length < 3 || segments[0] != RawPrefix)
        {
            return null;
        }

        await using var stream = await storage.DownloadAsync(key, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        var tenantSegment = segments.Length > 6 ? segments[2] : string.Empty;
        return TryDecrypt(segments[1], tenantSegment, ms.ToArray());
    }

    /// <summary>Deletes every archive object filed under the tenant (payloads and their sidecars). Pre-#142 objects carry no tenant and are not touched.</summary>
    public async Task<int> EraseTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
        {
            return 0;
        }

        var segment = tenantId.ToString("N");
        var keys = new List<string>();
        await foreach (var obj in browser.ListAsync(RawPrefix, ct).ConfigureAwait(false))
        {
            var parts = obj.Key.Split('/');
            if (parts.Length > 3 && parts[0] == RawPrefix && parts[2] == segment)
            {
                keys.Add(obj.Key);
            }
        }

        foreach (var key in keys)
        {
            await storage.DeleteAsync(key, ct).ConfigureAwait(false);
        }

        return keys.Count;
    }

    /// <summary>Deletes archive objects last written before <paramref name="cutoffUtc"/>. Only called when retention is explicitly enabled.</summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        var keys = new List<string>();
        await foreach (var obj in browser.ListAsync(RawPrefix, ct).ConfigureAwait(false))
        {
            if (obj.LastWriteUtc < cutoffUtc)
            {
                keys.Add(obj.Key);
            }
        }

        foreach (var key in keys)
        {
            await storage.DeleteAsync(key, ct).ConfigureAwait(false);
        }

        return keys.Count;
    }

    /// <summary>Runs <see cref="PruneAsync"/> with the configured retention; a no-op (0) when retention is off, which is the default.</summary>
    public Task<int> PruneConfiguredAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        settings.RetentionDays > 0 ? PruneAsync(nowUtc.AddDays(-settings.RetentionDays), ct) : Task.FromResult(0);

    private const string SealingSuffix = ".sealing";
    private const string MetaSuffix = ".meta.json";

    /// <summary>
    /// Opt-in admin job (#142): seals archive objects that were written as plaintext before <c>Storage:EncryptionKey</c> was set (including
    /// the pre-tenant-folder, unattributable ones), IN PLACE so every stored key (replay handler references, sidecars) stays valid.
    /// Idempotent. The archive can be the only copy of billable provider data (ADR 0007), so each object is written to a
    /// <c>.sealing</c> temp copy, verified, then swapped in and verified again; an interrupted run is repaired at the start of the next one.
    /// Requires an encryption key; throws <see cref="InvalidOperationException"/> otherwise so nothing is ever "sealed" without one.
    /// </summary>
    public async Task<SealResult> SealPlaintextAsync(CancellationToken ct = default)
    {
        if (!settings.Encrypts)
        {
            throw new InvalidOperationException("Set Storage:EncryptionKey before sealing the raw archive.");
        }

        var all = new List<string>();
        await foreach (var obj in browser.ListAsync(RawPrefix, ct).ConfigureAwait(false))
        {
            all.Add(obj.Key);
        }

        var recovered = 0;
        foreach (var temp in all.Where(k => k.EndsWith(SealingSuffix, StringComparison.Ordinal)))
        {
            if (await RecoverAsync(temp, ct).ConfigureAwait(false))
            {
                recovered++;
            }
        }

        int sealedCount = 0, already = 0, skipped = 0;
        foreach (var key in all.Where(k => !k.EndsWith(SealingSuffix, StringComparison.Ordinal) && !k.EndsWith(MetaSuffix, StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            var segments = key.Split('/');
            if (segments.Length < 5 || segments[0] != RawPrefix)
            {
                skipped++;
                continue;
            }

            var content = await TryReadRawAsync(key, ct).ConfigureAwait(false);
            if (content is null || content.Length == 0)
            {
                skipped++;
                continue;
            }

            if (IRawPayloadDecryptor.IsEncrypted(content))
            {
                already++;
                continue;
            }

            var tenant = segments.Length > 6 && Guid.TryParseExact(segments[2], "N", out var parsed) ? parsed : (Guid?)null;
            var aad = Aad(tenant, segments[1]);
            var sealedBytes = System.Text.Encoding.ASCII.GetBytes(IRawPayloadDecryptor.Magic + SecretBox.SealV2(content, settings.Ring, aad));

            // 1. temp copy first, so a crash while the original is rewritten never loses the only plaintext.
            await WriteAsync(key + SealingSuffix, sealedBytes, ct).ConfigureAwait(false);
            if (!Same(TryDecrypt(segments[1], tenant is { } t1 ? t1.ToString("N") : string.Empty, sealedBytes), content))
            {
                await storage.DeleteAsync(key + SealingSuffix, ct).ConfigureAwait(false);
                skipped++;
                continue;
            }

            // 2. swap in, then verify what is on disk before dropping the temp copy.
            await WriteAsync(key, sealedBytes, ct).ConfigureAwait(false);
            var written = await TryReadRawAsync(key, ct).ConfigureAwait(false);
            if (written is null || !Same(TryDecrypt(segments[1], tenant is { } t2 ? t2.ToString("N") : string.Empty, written), content))
            {
                skipped++;   // keep the temp copy; the next run repairs the original from it
                continue;
            }

            await storage.DeleteAsync(key + SealingSuffix, ct).ConfigureAwait(false);
            sealedCount++;
        }

        return new SealResult(sealedCount, already, skipped, recovered);
    }

    /// <summary>Repairs an interrupted seal: restores the original from its temp copy when the original is missing, empty or unreadable.</summary>
    private async Task<bool> RecoverAsync(string tempKey, CancellationToken ct)
    {
        var key = tempKey[..^SealingSuffix.Length];
        var segments = key.Split('/');
        var tempBytes = await TryReadRawAsync(tempKey, ct).ConfigureAwait(false);
        var original = await TryReadRawAsync(key, ct).ConfigureAwait(false);
        var tenantSegment = segments.Length > 6 ? segments[2] : string.Empty;

        var originalOk = original is { Length: > 0 }
            && (!IRawPayloadDecryptor.IsEncrypted(original) || TryOpen(segments, tenantSegment, original));
        var repaired = false;
        if (!originalOk && tempBytes is { Length: > 0 } && TryOpen(segments, tenantSegment, tempBytes))
        {
            await WriteAsync(key, tempBytes, ct).ConfigureAwait(false);
            repaired = true;
        }

        if (originalOk || repaired)
        {
            await storage.DeleteAsync(tempKey, ct).ConfigureAwait(false);
        }

        return repaired;
    }

    private bool TryOpen(string[] segments, string tenantSegment, byte[] content)
    {
        try
        {
            return segments.Length > 1 && TryDecrypt(segments[1], tenantSegment, content) is not null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<byte[]?> TryReadRawAsync(string key, CancellationToken ct)
    {
        try
        {
            await using var stream = await storage.DownloadAsync(key, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private async Task WriteAsync(string key, byte[] content, CancellationToken ct)
    {
        using var stream = new MemoryStream(content, writable: false);
        await storage.UploadAsync(key, stream, "application/json", ct).ConfigureAwait(false);
    }

    private static bool Same(byte[]? a, byte[] b) => a is not null && a.AsSpan().SequenceEqual(b);
}
