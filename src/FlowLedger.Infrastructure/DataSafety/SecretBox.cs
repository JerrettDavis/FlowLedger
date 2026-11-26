using System.Security.Cryptography;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Keys used to seal and open stored secrets (#156). <see cref="Current"/> seals; opening also tries <see cref="Fallbacks"/>
/// (previous and legacy keys) so rotating or renaming the configured key never strands existing rows.
/// </summary>
internal sealed class SecretKeyRing
{
    private readonly Dictionary<string, byte[]> _byId = [];

    public SecretKeyRing(byte[]? current, IEnumerable<byte[]>? fallbacks = null)
    {
        Current = current;
        Fallbacks = [.. (fallbacks ?? []).Where(k => current is null || !k.AsSpan().SequenceEqual(current)).DistinctBy(SecretBox.KeyId)];
        foreach (var k in AllKeys)
        {
            _byId[SecretBox.KeyId(k)] = k;
        }
    }

    public byte[]? Current { get; }

    public IReadOnlyList<byte[]> Fallbacks { get; }

    public bool CanSeal => Current is not null;

    public IEnumerable<byte[]> AllKeys => Current is null ? Fallbacks : Fallbacks.Prepend(Current);

    public byte[]? Find(string keyId) => _byId.GetValueOrDefault(keyId);

    /// <summary>Builds a ring from base64 settings. The current key is strict (a wrong length is a config error); fallbacks are
    /// lenient (comma separated, bad entries ignored) so a stale "previous" setting can never stop the host starting.</summary>
    public static SecretKeyRing From(string? currentBase64, params string?[] fallbackBase64)
    {
        var current = SecretBox.ParseKey(currentBase64);
        var fallbacks = fallbackBase64
            .SelectMany(v => (v ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(SecretBox.TryParseKey)
            .OfType<byte[]>()
            .ToList();
        return new SecretKeyRing(current, fallbacks);
    }
}

/// <summary>
/// AES-256-GCM envelopes for stored secrets. v1 is <c>"v1:" + base64(nonce|tag|ciphertext)</c> (no AAD, single key). v2 is
/// <c>"v2:{keyId}:" + base64(nonce|tag|ciphertext)</c> where keyId is the first 4 bytes of SHA-256(key) in hex and the AAD binds
/// the ciphertext to its tenant, purpose and row, so a value copied to another tenant or row fails to open. v1 stays readable.
/// </summary>
internal static class SecretBox
{
    /// <summary>First 4 bytes of SHA-256(key), hex: a stable, non-secret identifier that lets Open choose the right key.</summary>
    public static string KeyId(byte[] key) => Convert.ToHexString(SHA256.HashData(key), 0, 4).ToLowerInvariant();

    public static byte[]? TryParseKey(string? base64)
    {
        try
        {
            return ParseKey(base64);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The additional authenticated data for a secret: tenant, purpose and (when the table has many rows per tenant) the row.</summary>
    public static byte[] Aad(Guid tenantId, string purpose, Guid? rowId = null) =>
        System.Text.Encoding.UTF8.GetBytes($"flowledger.secret.v2|{tenantId:D}|{purpose}|{rowId:D}");

    public static string SealV2(string plain, SecretKeyRing ring, byte[] aad) => SealV2(System.Text.Encoding.UTF8.GetBytes(plain), ring, aad);

    public static string SealV2(byte[] data, SecretKeyRing ring, byte[] aad)
    {
        var key = ring.Current ?? throw new InvalidOperationException("No encryption key is configured.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, data, cipher, tag, aad);
        return $"v2:{KeyId(key)}:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Opens a v2 envelope to raw bytes (key chosen by id, AAD enforced). Null when it cannot be opened.</summary>
    public static byte[]? OpenV2Bytes(string stored, SecretKeyRing ring, byte[] aad)
    {
        if (!stored.StartsWith("v2:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = stored.Split(':', 3);
        return parts.Length == 3 && ring.Find(parts[1]) is { } key ? OpenCoreBytes(parts[2], key, aad) : null;
    }

    /// <summary>Opens a v2 value (key chosen by id, AAD enforced) or a v1 value (every ring key tried, no AAD). Null when it cannot be opened.</summary>
    public static string? Open(string stored, SecretKeyRing ring, byte[] aad)
    {
        if (stored.StartsWith("v2:", StringComparison.Ordinal))
        {
            var parts = stored.Split(':', 3);
            return parts.Length == 3 && ring.Find(parts[1]) is { } key ? OpenCore(parts[2], key, aad) : null;
        }

        foreach (var key in ring.AllKeys)
        {
            if (Open(stored, key) is { } plain)
            {
                return plain;
            }
        }

        return null;
    }

    /// <summary>True when the value is not a v2 envelope sealed under the ring's current key (so <see cref="Reseal"/> would change it).</summary>
    public static bool NeedsReseal(string stored, SecretKeyRing ring) =>
        ring.Current is { } current && !stored.StartsWith($"v2:{KeyId(current)}:", StringComparison.Ordinal);

    /// <summary>Re-encrypts a value as v2 under the current key (the rotation step). Null when the value cannot be opened with any ring key.</summary>
    public static string? Reseal(string stored, SecretKeyRing ring, byte[] aad)
        => Open(stored, ring, aad) is { } plain ? SealV2(plain, ring, aad) : null;

    private static string? OpenCore(string b64, byte[] key, byte[] aad)
        => OpenCoreBytes(b64, key, aad) is { } plain ? System.Text.Encoding.UTF8.GetString(plain) : null;

    private static byte[]? OpenCoreBytes(string b64, byte[] key, byte[] aad)
    {
        try
        {
            var raw = Convert.FromBase64String(b64);
            var plain = new byte[raw.Length - 28];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain, aad);
            return plain;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        var key = Convert.FromBase64String(base64.Trim());
        return key.Length == 32 ? key : throw new InvalidOperationException("The encryption key must be 32 bytes, base64 encoded.");
    }

    public static string Seal(string plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, data, cipher, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Returns null when an encrypted value cannot be opened (key missing or changed), never throws.</summary>
    public static string? Open(string stored, byte[]? key)
    {
        if (!stored.StartsWith("v1:", StringComparison.Ordinal) || key is null)
        {
            return null;
        }

        try
        {
            var raw = Convert.FromBase64String(stored[3..]);
            var plain = new byte[raw.Length - 28];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
