using System.Security.Cryptography;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Tiny AES-256-GCM envelope for stored secrets ("v1:" + base64(nonce|tag|ciphertext)).</summary>
internal static class SecretBox
{
    public static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        var key = Convert.FromBase64String(base64.Trim());
        return key.Length == 32 ? key : throw new InvalidOperationException("HomeAssistant:EncryptionKey must be 32 bytes, base64 encoded.");
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
