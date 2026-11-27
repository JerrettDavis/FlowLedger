namespace FlowLedger.Web.Shared;

/// <summary>Plain-language age of the last sync for the app bar ("Synced 5 min ago").</summary>
public static class SyncAge
{
    public static string Describe(DateTimeOffset lastSyncedAt, DateTimeOffset now)
    {
        var age = now - lastSyncedAt;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Synced just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"Synced {(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"Synced {(int)age.TotalHours} h ago";
        }

        return $"Synced {(int)age.TotalDays} d ago";
    }
}
