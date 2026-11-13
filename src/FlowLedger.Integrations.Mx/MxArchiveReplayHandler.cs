using System.Net;
using System.Text.Json;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Serves previously archived MX responses (raw/MX/**.json + .meta.json sidecars) instead of calling MX.
/// Plug it in as the primary handler of the <see cref="HttpClient"/> behind <c>MxApiClient</c> and the normal
/// provider, mapper and sync run unchanged, so a mapping bug can be fixed and data re-ingested at zero API cost.
/// Lookup is by (method, path+query); when the same request was archived repeatedly the NEWEST capture wins.
/// An un-archived request returns 404 (never reaches the network).
/// </summary>
public sealed class MxArchiveReplayHandler : HttpMessageHandler
{
    private readonly Dictionary<(string Method, string Path), string> _index = new();

    /// <param name="mxArchiveRoot">Directory containing the archived MX files, e.g. <c>{objects}/raw/MX</c>.</param>
    public MxArchiveReplayHandler(string mxArchiveRoot)
    {
        foreach (var meta in Directory.EnumerateFiles(mxArchiveRoot, "*.meta.json", SearchOption.AllDirectories)
                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)) // ticks-prefixed → chronological
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(meta));
            var root = doc.RootElement;
            if (root.GetProperty("statusCode").GetInt32() is < 200 or >= 300)
            {
                continue; // replay successes only
            }

            var payload = meta[..^".meta.json".Length] + ".json";
            if (File.Exists(payload))
            {
                _index[(root.GetProperty("method").GetString()!, root.GetProperty("pathAndQuery").GetString()!)] = payload;
            }
        }
    }

    /// <summary>Number of distinct archived requests available for replay.</summary>
    public int Count => _index.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = (request.Method.Method, request.RequestUri!.PathAndQuery);
        if (!_index.TryGetValue(key, out var file))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }

        var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }
}
