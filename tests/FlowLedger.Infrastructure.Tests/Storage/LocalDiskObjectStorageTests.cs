using System.Text;
using FlowLedger.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="LocalDiskObjectStorage"/>.
/// Each test uses an isolated temporary directory and cleans up after itself.
/// </summary>
public sealed class LocalDiskObjectStorageTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"FlowLedger-ObjStore-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private LocalDiskObjectStorage CreateStorage() =>
        new(
            Options.Create(new ObjectStorageOptions { RootPath = _rootPath }),
            NullLogger<LocalDiskObjectStorage>.Instance);

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_then_Download_roundtrips()
    {
        var storage = CreateStorage();
        var key = "reports/2026/q1.txt";
        var content = "Hello, FlowLedger!"u8.ToArray();

        await storage.UploadAsync(key, new MemoryStream(content), "text/plain");

        await using var downloaded = await storage.DownloadAsync(key);
        var result = new MemoryStream();
        await downloaded.CopyToAsync(result);

        result.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Upload_returns_file_uri()
    {
        var storage = CreateStorage();
        var uri = await storage.UploadAsync(
            "foo/bar.json",
            new MemoryStream("{}u8.ToArray()"u8.ToArray()),
            "application/json");

        uri.Scheme.Should().Be("file");
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_removes_object()
    {
        var storage = CreateStorage();
        var key = "to-delete/file.bin";

        await storage.UploadAsync(key, new MemoryStream([1, 2, 3]), "application/octet-stream");
        await storage.DeleteAsync(key);

        var act = () => storage.DownloadAsync(key);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Delete_is_idempotent_for_missing_key()
    {
        var storage = CreateStorage();

        // Deleting a non-existent key must not throw.
        var act = () => storage.DeleteAsync("does/not/exist.txt");
        await act.Should().NotThrowAsync();
    }

    // ── Download missing ──────────────────────────────────────────────────────

    [Fact]
    public async Task Download_throws_FileNotFoundException_for_missing_key()
    {
        var storage = CreateStorage();

        var act = () => storage.DownloadAsync("missing/object.txt");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ── Path traversal security ───────────────────────────────────────────────

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("legit/../../../etc/shadow")]
    [InlineData("a/b/../../../../../../etc/hosts")]
    public async Task Rejects_path_traversal_keys(string maliciousKey)
    {
        var storage = CreateStorage();

        var uploadAct = () => storage.UploadAsync(maliciousKey, new MemoryStream([0]), "application/octet-stream");
        await uploadAct.Should().ThrowAsync<ArgumentException>(
            $"key '{maliciousKey}' should be rejected as a path-traversal attempt");

        var downloadAct = () => storage.DownloadAsync(maliciousKey);
        await downloadAct.Should().ThrowAsync<ArgumentException>();

        var deleteAct = () => storage.DeleteAsync(maliciousKey);
        await deleteAct.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Rejects_absolute_path_key()
    {
        var storage = CreateStorage();
        var absoluteKey = Path.IsPathRooted("/etc/passwd") ? "/etc/passwd" : @"C:\Windows\System32\drivers\etc\hosts";

        var act = () => storage.UploadAsync(absoluteKey, new MemoryStream([0]), "application/octet-stream");
        await act.Should().ThrowAsync<ArgumentException>("absolute paths must be rejected");
    }

    [Fact]
    public async Task Rejects_empty_key()
    {
        var storage = CreateStorage();

        var act = () => storage.UploadAsync("", new MemoryStream([0]), "application/octet-stream");
        await act.Should().ThrowAsync<ArgumentException>("empty keys must be rejected");
    }

    // ── Sub-directory creation ────────────────────────────────────────────────

    [Fact]
    public async Task Upload_creates_nested_directories()
    {
        var storage = CreateStorage();
        var key = "a/b/c/d/file.txt";

        // Should not throw even though the intermediate directories don't exist.
        var act = () => storage.UploadAsync(key, new MemoryStream("data"u8.ToArray()), "text/plain");
        await act.Should().NotThrowAsync();

        await using var stream = await storage.DownloadAsync(key);
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Be("data");
    }
}
