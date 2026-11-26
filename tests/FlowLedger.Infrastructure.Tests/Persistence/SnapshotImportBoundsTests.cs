using System.IO.Compression;
using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>Zip-bomb guard counts the bytes actually written while extracting, not the sizes the archive declares.</summary>
public sealed class SnapshotImportBoundsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-zip-" + Guid.NewGuid().ToString("N"));

    public SnapshotImportBoundsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static MemoryStream ZipOf(string name, int bytes)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(new byte[bytes]);
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Extraction_aborts_mid_stream_once_the_real_byte_count_exceeds_the_budget()
    {
        using var ms = ZipOf("big.csv", 500_000);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        long written = 0;

        var act = () => SnapshotImport.ExtractBounded(archive.Entries[0], Path.Combine(_dir, "big.csv"), ref written, maxTotalBytes: 10_000);

        act.Should().Throw<InvalidDataException>().WithMessage("*expands*");
        written.Should().BeLessThan(500_000, "extraction must stop as soon as the budget is exceeded");
    }

    [Fact]
    public void Extraction_within_the_budget_writes_the_file_and_accumulates_the_total()
    {
        using var ms = ZipOf("ok.csv", 4_000);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        long written = 0;

        SnapshotImport.ExtractBounded(archive.Entries[0], Path.Combine(_dir, "ok.csv"), ref written, maxTotalBytes: 10_000);

        written.Should().Be(4_000);
        new FileInfo(Path.Combine(_dir, "ok.csv")).Length.Should().Be(4_000);
    }

    [Fact]
    public async Task Import_with_an_oversized_bundle_is_refused_without_touching_the_database()
    {
        using var ms = ZipOf("big.csv", 50_000);

        // The connection string is never opened: the bundle is rejected during extraction.
        var result = await SnapshotImport.ImportZipAsync("Host=invalid", ms, _dir, maxUncompressedBytes: 10_000);

        result.Ok.Should().BeFalse();
        string.Join(" ", result.Problems).Should().Contain("expands");
    }
}
