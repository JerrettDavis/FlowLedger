using Microsoft.Extensions.Configuration;

namespace FlowLedger.Integrations.Mx;

/// <summary>
/// Decides which verified MX webhook events are worth a sync (#120). Every accepted event costs a billable provider read, and MX also
/// sends events that carry no new financial data (connection-status changes, identity or statement notices, start-of-aggregation).
/// The default is a deliberately conservative allow-list of the MX Platform webhook <c>type</c> values that mean transactions or
/// accounts changed: <c>aggregation</c> (a member aggregation completed), <c>history</c> (historical transactions became available),
/// <c>account</c>/<c>accounts</c> and <c>transaction</c>/<c>transactions</c> (created/updated). Anything else, including an unparseable
/// type ("UNKNOWN"), is acknowledged and ignored; the scheduled sync stays the backstop.
/// NOTE: the repository has no MX documentation or captured payloads, so this list comes from the MX Platform webhook event names as
/// understood by the author and must be checked against the MX documentation for the enabled products before production use.
/// Override with <c>Mx:WebhookSyncEventTypes</c>: a comma-separated string (<c>Mx__WebhookSyncEventTypes=aggregation,history</c>) or a JSON
/// array, case-insensitive. The single value <c>none</c> disables webhook-triggered syncs (an empty array or value cannot express
/// "nothing" because configuration drops empty arrays, so it falls back to the default).
/// </summary>
public static class MxWebhookEvents
{
    public const string ConfigKey = "Mx:WebhookSyncEventTypes";

    public static readonly IReadOnlyList<string> DefaultSyncEventTypes =
        ["aggregation", "history", "account", "accounts", "transaction", "transactions"];

    /// <summary>The configured allow-list, or <see cref="DefaultSyncEventTypes"/> when nothing usable is configured.</summary>
    public static IReadOnlyList<string> FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(ConfigKey);
        var values = section.GetChildren().Select(c => c.Value).Append(section.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        if (values.Count == 0)
        {
            return DefaultSyncEventTypes;
        }

        return values.All(v => v.Equals("none", StringComparison.OrdinalIgnoreCase)) ? [] : values;
    }

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
