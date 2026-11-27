using FlowLedger.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Storage;

/// <summary>
/// Local-disk implementation of <see cref="IObjectStorage"/>.
///
/// Security: keys are sanitised before any file-system access.  The resolved full path
/// must be rooted inside <see cref="ObjectStorageOptions.RootPath"/>; any key that would
/// escape (path traversal, absolute paths, embedded <c>..</c> segments) is rejected with
/// <see cref="ArgumentException"/>.
///
/// S3/MinIO can be substituted by replacing this registration in
/// <c>DependencyInjection.cs</c> — no callers are affected.
/// </summary>
internal sealed class LocalDiskObjectStorage : IObjectStorage, IObjectStorageBrowser
{
    private readonly string _rootPath;
    private readonly ILogger<LocalDiskObjectStorage> _logger;

    public LocalDiskObjectStorage(
        IOptions<ObjectStorageOptions> options,
        ILogger<LocalDiskObjectStorage> logger)
    {
        _rootPath = Path.GetFullPath(options.Value.RootPath);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Uri> UploadAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        var filePath = ResolveAndValidatePath(key);

        var directory = Path.GetDirectoryName(filePath)!;
        CreateDirectoryOwnerOnly(directory);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile;
        }

        await using var fileStream = new FileStream(filePath, options);

        await content.CopyToAsync(fileStream, ct);

        _logger.LogDebug("ObjectStorage: uploaded key={Key} contentType={ContentType}", key, contentType);

        return new Uri($"file://{filePath.Replace('\\', '/')}");
    }

    /// <summary>Directories are 0700 and files 0600 on Unix (#142): the archive holds bank data, so only the service account may read it.</summary>
    internal const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private void CreateDirectoryOwnerOnly(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);   // ACLs are inherited from the parent on Windows; there is no mode to set
            return;
        }

        Directory.CreateDirectory(directory, OwnerOnlyDirectory);
        TightenIfWider(_rootPath);   // a root created by an older version or by the volume mount keeps its mode otherwise
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private void TightenIfWider(string directory)
    {
        try
        {
            if (File.GetUnixFileMode(directory) != OwnerOnlyDirectory)
            {
                File.SetUnixFileMode(directory, OwnerOnlyDirectory);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not the owner (for example a root-owned volume): the operator has to chmod it; do not fail the write.
            _logger.LogWarning(ex, "ObjectStorage: could not restrict the storage root to its owner; restrict it manually (chmod 700).");
        }
    }

    /// <inheritdoc/>
    public async Task<Stream> DownloadAsync(string key, CancellationToken ct = default)
    {
        var filePath = ResolveAndValidatePath(key);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Object with key '{key}' does not exist.", filePath);
        }

        // Read into a MemoryStream so the file handle is released immediately.
        var buffer = await File.ReadAllBytesAsync(filePath, ct);
        return new MemoryStream(buffer, writable: false);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var filePath = ResolveAndValidatePath(key);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogDebug("ObjectStorage: deleted key={Key}", key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StoredObject> ListAsync(
        string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // The prefix is validated like any key (no rooted path, no "..", must resolve inside the root) before the directory is walked.
        var start = ResolveAndValidatePath(prefix.TrimEnd('/', '\\'));
        if (!Directory.Exists(start))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(_rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = Path.GetRelativePath(_rootPath, full).Replace('\\', '/');
            yield return new StoredObject(key, new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero));
            await Task.Yield();
        }
    }

    // ── Path validation ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a key to an absolute file path and validates it stays within <see cref="_rootPath"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the key is empty, absolute, contains <c>..</c> segments, or resolves
    /// outside the root (path-traversal attempt).
    /// </exception>
    private string ResolveAndValidatePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Object storage key must not be empty.", nameof(key));
        }

        // Reject explicitly absolute paths (Windows or Unix style).
        if (Path.IsPathRooted(key))
        {
            throw new ArgumentException(
                $"Object storage key must be a relative path. Got: '{key}'", nameof(key));
        }

        // Reject any segment that is exactly ".." (handles cross-platform dot-dot segments).
        var segments = key.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(s => s == ".."))
        {
            throw new ArgumentException(
                $"Object storage key must not contain '..' segments. Got: '{key}'", nameof(key));
        }

        // Resolve the full path and verify containment.
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, key));

        // GetFullPath normalises the path; the containment check catches any remaining tricks.
        var rootWithSeparator = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Object storage key '{key}' resolves outside the storage root. Possible path-traversal attempt.",
                nameof(key));
        }

        return fullPath;
    }
}
