namespace FlowLedger.Integrations.Quiltt;

/// <summary>
/// Quiltt settings (config section <c>Quiltt</c>). Quiltt is an embedded-connector aggregator: each FlowLedger tenant gets its own
/// Quiltt "profile", and the browser opens the Quiltt Connector with a short-lived session token minted here, so users link banks
/// without leaving the app. <see cref="ApiKeySecret"/> and <see cref="WebhookSecret"/> belong in user-secrets / env vars only.
/// </summary>
public sealed class QuilttOptions
{
    public const string SectionName = "Quiltt";

    /// <summary>Turns the Quiltt connector on. Sandbox keeps the process on sandbox data; production data needs Environment=production.</summary>
    public bool Enabled { get; set; }

    /// <summary>"sandbox" (default) or "production". Production makes the process hold real bank data (see DataEnvironmentGuard).</summary>
    public string Environment { get; set; } = "sandbox";

    /// <summary>Server-side API key secret (Quiltt dashboard → API Keys). Never sent to the browser.</summary>
    public string? ApiKeySecret { get; set; }

    /// <summary>Public Connector id (con_...) the browser opens. Not a secret.</summary>
    public string? ConnectorId { get; set; }

    /// <summary>Secret used to verify the <c>Quiltt-Signature</c> header on inbound webhooks.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Session (auth) API base. Override for tests / a mock server.</summary>
    public string AuthBaseUrl { get; set; } = "https://auth.quiltt.io/v1/";

    /// <summary>GraphQL API base. Override for tests / a mock server.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.quiltt.io/v1/";

    /// <summary>Browser script that hosts the Connector. Override to pin a version or self-host.</summary>
    public string ConnectorScriptUrl { get; set; } = "https://cdn.quiltt.io/v1/connector.js";

    /// <summary>History requested on the first sync of an account (no cursor).</summary>
    public int InitialHistoryDays { get; set; } = 730;

    /// <summary>Days re-read before the cursor on incremental syncs, to catch pending→posted changes (fingerprint dedupe keeps it safe).</summary>
    public int OverlapDays { get; set; } = 14;

    /// <summary>GraphQL page size.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Safety cap on GraphQL pages per list per sync.</summary>
    public int MaxPages { get; set; } = 100;

    /// <summary>
    /// How long to back off after an HTTP 429 when Quiltt sends no Retry-After header (verified: it does not, for the per-profile
    /// hourly session quota).
    /// </summary>
    public TimeSpan RateLimitFallback { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Webhook timestamp tolerance, to reject replays of old captured deliveries.</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);

    public bool IsProduction => string.Equals(Environment, "production", StringComparison.OrdinalIgnoreCase);
}
