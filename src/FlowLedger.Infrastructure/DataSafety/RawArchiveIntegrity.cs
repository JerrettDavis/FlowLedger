using System.Security.Cryptography;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Hash check of the raw provider-payload archive. Plain objects are keyed
/// <c>...-{operation}-{sha256[0..16]}.json</c> (see <c>ObjectStorageRawPayloadArchive</c>), so a bit-flipped or truncated object
/// no longer hashes to its key. Sealed (encrypted) objects cannot be checked without the key ring and are only counted.
/// </summary>
internal static class RawArchiveIntegrity
{
    internal sealed record Result(int Verified, int SkippedEncrypted, IReadOnlyList<string> Problems);

    public static Result Verify(string objectsDir)
    {
        var problems = new List<string>();
        var rawRoot = Path.Combine(objectsDir, "raw");
        if (!Directory.Exists(rawRoot))
        {
            return new Result(0, 0, problems);
        }

        var verified = 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(rawRoot, "*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stem = name[..^".json".Length];
            var expected = stem[(stem.LastIndexOf('-') + 1)..];
            var bytes = File.ReadAllBytes(file);
            if (StartsWithMagic(bytes))
            {
                skipped++;
                continue;
            }

            var actual = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            if (expected.Length == 16 && string.Equals(actual, expected, StringComparison.Ordinal))
            {
                verified++;
            }
            else
            {
                problems.Add($"Raw archive object {Path.GetRelativePath(objectsDir, file).Replace('\\', '/')} does not match its content hash.");
            }
        }

        return new Result(verified, skipped, problems);
    }

    private static bool StartsWithMagic(byte[] bytes)
    {
        var magic = System.Text.Encoding.ASCII.GetBytes(IRawPayloadDecryptor.Magic);
        return bytes.AsSpan().StartsWith(magic);
    }
}
