using System.Security.Cryptography;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.LocalAi;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Ai.Local;

/// <summary>
/// Owns the model directory: reports what is installed, downloads catalog models (explicit, owner-initiated, one at a time,
/// size- and SHA-256-verified, atomically moved into place) and removes them. Nothing downloads at startup.
/// </summary>
public sealed class LocalModelManager(
    LocalAiOptions options,
    IHttpClientFactory http,
    ILogger<LocalModelManager> logger,
    Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null) : ILocalAiModels
{
    public const string HttpClientName = "local-ai-models";
    private static readonly string[] AllowedHosts = ["huggingface.co", "cdn-lfs.huggingface.co", "cas-bridge.xethub.hf.co"];

    private readonly object _gate = new();
    private LocalAiDownload? _download;
    private bool _running;

    public string Directory
    {
        get
        {
            var dir = !string.IsNullOrWhiteSpace(options.ModelDirectory)
                ? options.ModelDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLedger", "models");
            return Path.GetFullPath(dir);
        }
    }

    public LocalAiDownload? Download { get { lock (_gate) { return _download; } } }

    public string PathOf(ModelFile f) => Path.Combine(Directory, f.Name);

    /// <summary>Installed = every file present with the expected size (hashes are verified once, at download).</summary>
    public bool IsInstalled(CatalogModel m) => m.Files.All(f =>
    {
        var info = new FileInfo(PathOf(f));
        return info.Exists && (f.SizeBytes <= 0 || info.Length == f.SizeBytes);
    });

    public bool TryStartDownload(string modelId, out string? error)
    {
        error = null;
        var model = ModelCatalog.Find(modelId);
        if (model is null)
        {
            error = "Unknown model.";
            return false;
        }

        if (!options.AllowModelDownload)
        {
            error = "Model downloads are disabled (LocalAi:AllowModelDownload=false). Copy the files into " + Directory + " instead.";
            return false;
        }

        lock (_gate)
        {
            if (_running)
            {
                error = "Another download is already running.";
                return false;
            }

            _running = true;
            _download = new LocalAiDownload(model.Id, "Downloading", 0, model.SizeBytes, null);
        }

        // The host's ApplicationStopping token cancels the download on shutdown; a stalled read is cut off by the inactivity timeout.
        var shutdown = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _ = Task.Run(() => RunAsync(model, shutdown));
        return true;
    }

    public bool Remove(string modelId)
    {
        var model = ModelCatalog.Find(modelId);
        if (model is null)
        {
            return false;
        }

        var removed = false;
        foreach (var f in model.Files)
        {
            var path = PathOf(f);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removed = true;
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Could not delete model file {File} (in use?)", f.Name);
                }
            }
        }

        return removed;
    }

    private async Task RunAsync(CatalogModel model, CancellationToken ct)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            long done = 0;
            foreach (var file in model.Files)
            {
                var target = PathOf(file);
                if (File.Exists(target) && new FileInfo(target).Length == file.SizeBytes)
                {
                    done += file.SizeBytes;
                    continue;
                }

                await DownloadFileAsync(model, file, done, ct);
                done += file.SizeBytes;
            }

            lock (_gate) { _download = new LocalAiDownload(model.Id, "Done", model.SizeBytes, model.SizeBytes, null); }
            logger.LogInformation("Local AI model {Model} installed in {Dir}", model.Id, Directory);
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException && ct.IsCancellationRequested
                ? "Download cancelled (the host is shutting down)."
                : ex.Message;
            logger.LogWarning(ex, "Local AI model download failed for {Model}", model.Id);
            lock (_gate) { _download = new LocalAiDownload(model.Id, "Failed", 0, model.SizeBytes, message); }
        }
        finally
        {
            lock (_gate) { _running = false; }
        }
    }

    private async Task DownloadFileAsync(CatalogModel model, ModelFile file, long alreadyDone, CancellationToken shutdown)
    {
        var uri = new Uri(file.Url);
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Catalog URL is not an allowed https host.");
        }

        var tmp = PathOf(file) + ".partial";
        var idle = options.DownloadInactivityTimeout > TimeSpan.Zero ? options.DownloadInactivityTimeout : Timeout.InfiniteTimeSpan;

        // One linked source: host shutdown, plus an inactivity timer that is re-armed after every successful read.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        cts.CancelAfter(idle);
        try
        {
            await FetchToPartialAsync(model, file, alreadyDone, uri, tmp, idle, cts);
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Download of {file.Name} stalled: no data for {idle.TotalSeconds:0.#}s.");
        }
        finally
        {
            // Never leave a half-written file behind on any failure (the successful path moves it into place first).
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort: a lingering .partial is overwritten by the next attempt
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task FetchToPartialAsync(
        CatalogModel model, ModelFile file, long alreadyDone, Uri uri, string tmp, TimeSpan idle, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        using var client = http.CreateClient(HttpClientName);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // Redirects (HF -> CDN) are followed by the handler; refuse to end up anywhere unexpected.
        var finalHost = response.RequestMessage?.RequestUri?.Host ?? uri.Host;
        if (!AllowedHosts.Any(h => finalHost.Equals(h, StringComparison.OrdinalIgnoreCase) || finalHost.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
            && !finalHost.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Download redirected to an unexpected host: " + finalHost);
        }

        using var sha = SHA256.Create();
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                cts.CancelAfter(idle); // progress: re-arm the inactivity timer
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                sha.TransformBlock(buffer, 0, n, null, 0);
                got += n;
                if (got > file.SizeBytes + 1024)
                {
                    throw new InvalidDataException("Download is larger than the catalog says; aborting.");
                }

                lock (_gate) { _download = new LocalAiDownload(model.Id, "Downloading", alreadyDone + got, model.SizeBytes, null); }
            }

            sha.TransformFinalBlock([], 0, 0);
        }

        var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Checksum mismatch for {file.Name}; the file was discarded.");
        }

        File.Move(tmp, PathOf(file), overwrite: true);
    }
}
