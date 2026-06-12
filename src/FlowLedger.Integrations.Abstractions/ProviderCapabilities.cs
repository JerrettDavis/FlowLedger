namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// Feature flags describing what a particular provider implementation supports.
/// Callers should check before invoking optional operations.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,

    /// <summary>Provider can list accounts.</summary>
    Accounts = 1 << 0,

    /// <summary>Provider supports incremental transaction fetch with a cursor.</summary>
    IncrementalTransactions = 1 << 1,

    /// <summary>Provider supports full (non-incremental) transaction history.</summary>
    FullTransactionHistory = 1 << 2,

    /// <summary>Provider can verify inbound webhook signatures.</summary>
    WebhookVerification = 1 << 3,

    /// <summary>Provider can parse structured webhook event payloads.</summary>
    WebhookParsing = 1 << 4,

    /// <summary>Provider supports injected fault simulation (test doubles only).</summary>
    FaultSimulation = 1 << 5,
}
