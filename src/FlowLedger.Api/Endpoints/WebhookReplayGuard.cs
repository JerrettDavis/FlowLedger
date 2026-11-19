namespace FlowLedger.Api.Endpoints;

/// <summary>Remembers SHA-256 hashes of recently processed (already signature-verified) webhook bodies.</summary>
public static class WebhookReplayGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Seen = new();

    /// <summary>True when an identical body was already accepted inside the window (a replay). Per-process only.</summary>
    public static bool SeenRecently(byte[] body, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (Seen.Count >= MaxEntries)
        {
            foreach (var kv in Seen.Where(kv => t - kv.Value > Window).ToList())
            {
                Seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(kv.Key, kv.Value));
            }
        }

        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
        if (Seen.TryAdd(key, t))
        {
            return false; // first sighting (atomic: exactly one concurrent caller wins)
        }

        while (Seen.TryGetValue(key, out var prior))
        {
            if (t - prior <= Window)
            {
                return true; // replay inside the window
            }

            // Expired: exactly one caller replaces the stale entry and is treated as new.
            if (Seen.TryUpdate(key, t, prior))
            {
                return false;
            }
        }

        return !Seen.TryAdd(key, t);
    }
}
