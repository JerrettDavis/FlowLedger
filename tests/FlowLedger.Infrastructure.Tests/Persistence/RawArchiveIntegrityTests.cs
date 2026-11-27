using System.Security.Cryptography;
using System.Text;
using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

public sealed class RawArchiveIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flraw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string stemPrefix, byte[] content, string? hashOverride = null)
    {
        var dir = Path.Combine(_root, "raw", "mx", "_unattributed", "2026", "11", "27");
        Directory.CreateDirectory(dir);
        var hash = hashOverride ?? Convert.ToHexString(SHA256.HashData(content))[..16].ToLowerInvariant();
        var path = Path.Combine(dir, $"{stemPrefix}-sync-{hash}.json");
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Intact_objects_verify_and_meta_sidecars_are_ignored()
    {
        var path = Write("0000000000000000001", Encoding.UTF8.GetBytes("{\"a\":1}"));
        File.WriteAllText(path[..^".json".Length] + ".meta.json", "{}");

        var result = RawArchiveIntegrity.Verify(_root);

        result.Verified.Should().Be(1);
        result.Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_corrupted_object_is_reported()
    {
        var path = Write("0000000000000000002", Encoding.UTF8.GetBytes("{\"a\":1}"));
        File.WriteAllText(path, "{\"a\":2}");

        var result = RawArchiveIntegrity.Verify(_root);

        result.Verified.Should().Be(0);
        result.Problems.Should().ContainSingle().Which.Should().Contain("does not match its content hash");
    }

    [Fact]
    public void Sealed_objects_are_counted_but_not_hash_checked()
    {
        Write("0000000000000000003", Encoding.ASCII.GetBytes("FLRAWv2:opaque"), hashOverride: "0123456789abcdef");

        var result = RawArchiveIntegrity.Verify(_root);

        result.SkippedEncrypted.Should().Be(1);
        result.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Missing_archive_is_not_a_problem() => RawArchiveIntegrity.Verify(_root).Problems.Should().BeEmpty();
}
