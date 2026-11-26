using Serilog.Core;
using Serilog.Events;

namespace FlowLedger.Api.Logging;

/// <summary>
/// Serilog enricher that walks all log event properties and replaces the values of
/// properties whose names match known-sensitive patterns with "[REDACTED]".
///
/// NOT WIRED: this enricher is not registered in any logging pipeline (the app uses the default
/// Microsoft logger and OpenTelemetry, not Serilog), so it currently masks nothing at runtime; it
/// is exercised only by direct unit tests. Callers must avoid logging sensitive values in the first
/// place. If it is ever wired, properties whose name contains token / apikey / secret / password /
/// accountnumber / cardnumber / ssn / authorization / bearertoken / key would be masked.
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
