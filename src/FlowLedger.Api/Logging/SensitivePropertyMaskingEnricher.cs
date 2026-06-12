using Serilog.Core;
using Serilog.Events;

namespace FlowLedger.Api.Logging;

/// <summary>
/// Serilog enricher that walks all log event properties and replaces the values of
/// properties whose names match known-sensitive patterns with "[REDACTED]".
///
/// Defense-in-depth: callers should avoid logging sensitive values in the first place.
/// SECURITY convention: any new property whose name contains token / apikey / secret /
/// password / accountnumber / cardnumber / ssn / authorization / bearertoken / key
/// will be masked automatically. Do not rely on this as the primary protection.
/// </summary>
public sealed class SensitivePropertyMaskingEnricher : ILogEventEnricher
{
    private const string RedactedValue = "[REDACTED]";

    private static readonly string[] SensitiveNames =
    [
        "token", "apikey", "api_key", "secret", "password",
        "webhooksecret", "webhook_secret", "accountnumber",
        "account_number", "cardnumber", "card_number",
        "ssn", "authorization", "bearertoken", "bearer_token",
        "key"
    ];

    private static bool IsSensitive(string name) =>
        SensitiveNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (var key in logEvent.Properties.Keys.ToList())
        {
            if (IsSensitive(key))
            {
                logEvent.AddOrUpdateProperty(
                    new LogEventProperty(key, new ScalarValue(RedactedValue)));
            }
        }
    }
}
