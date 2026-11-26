using FlowLedger.Application.Features.Users;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Infrastructure.Storage;

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
}
