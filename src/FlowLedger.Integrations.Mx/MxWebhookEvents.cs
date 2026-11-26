namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Decides which verified MX webhook events are worth a sync (#120). Every accepted event costs a billable provider read, and MX also
/// sends events that carry no new financial data (connection-status changes, identity or statement notices, start-of-aggregation).
/// The default is a deliberately conservative allow-list of the events that mean transactions or accounts changed
/// (MX Platform webhook <c>type</c> values): <c>aggregation</c> (a member aggregation completed), <c>history</c> (historical
/// transactions became available), <c>account</c> and <c>transaction(s)</c> (an account or transactions were created/updated).
/// Anything else, including an unparseable type ("UNKNOWN"), is acknowledged and ignored; the scheduled sync stays the backstop.
/// Override with <c>Mx:WebhookSyncEventTypes</c> (array of type values, case-insensitive; an empty array syncs on nothing).
/// </summary>
public static class MxWebhookEvents
{
    public const string ConfigKey = "Mx:WebhookSyncEventTypes";

    public static readonly IReadOnlyList<string> DefaultSyncEventTypes =
        ["aggregation", "history", "account", "accounts", "transaction", "transactions"];

    public static bool IsSyncTrigger(string? eventType, IEnumerable<string>? configured = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return false;
        }

        var allowed = configured ?? DefaultSyncEventTypes;
        return allowed.Any(a => string.Equals(a?.Trim(), eventType.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
