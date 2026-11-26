using FlowLedger.Web.ApiClient;

namespace FlowLedger.Web.Connections;

/// <summary>What the browser auto-sync does after a failure.</summary>
/// <param name="BackoffUntil">Do not auto-sync before this time.</param>
/// <param name="StopUntilUserActs">The user must act (reconnect or sync manually) before auto-sync resumes.</param>
public sealed record AutoSyncDecision(DateTimeOffset BackoffUntil, bool StopUntilUserActs);

public static class AutoSyncPolicy
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);

    public static AutoSyncDecision AfterFailure(Exception ex, DateTimeOffset now, TimeSpan defaultBackoff)
    {
        if (ex is ApiClientException { Problem: { } p })
        {
            if (p.RequiresUserAction)
            {
                return new(DateTimeOffset.MaxValue, true);
            }

            if (p.RetryAfter is { } after)
            {
                return new(now + (after > MaxBackoff ? MaxBackoff : after), false);
            }
        }

        return new(now + defaultBackoff, false);
    }
}
