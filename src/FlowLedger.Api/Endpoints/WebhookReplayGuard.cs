namespace FlowLedger.Api.Endpoints;

/// <summary>Remembers SHA-256 hashes of recently processed (already signature-verified) webhook bodies.</summary>
public static class WebhookReplayGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Seen = new();

    public static bool SeenRecently(byte[] body, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (Seen.Count >= MaxEntries)
        {
            foreach (var kv in Seen.Where(kv => t - kv.Value > Window).ToList())
            {
                Seen.TryRemove(kv.Key, out _);
            }
        }

        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
        var prior = Seen.GetOrAdd(key, t);
        if (prior == t)
        {
            return false; // first sighting
        }

        if (t - prior > Window)
        {
            Seen[key] = t; // expired: treat as new
            return false;
        }

        return true;
    }
}
