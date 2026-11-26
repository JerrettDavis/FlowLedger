namespace FlowLedger.Integrations.Abstractions;

/// <summary>
/// Behavioural traits a connector declares about itself, so sync, host and UI code ask "what does this connector do" instead of
/// comparing provider-name strings (#89, #100). Each provider project owns its own declaration.
/// </summary>
[Flags]
public enum ConnectorTraits
{
    None = 0,

    /// <summary>
    /// Credentials are per tenant (or absent): any tenant may use the connector. Without this flag the connector runs on operator-held,
    /// instance-wide credentials and may only act for the household tenant.
    /// </summary>
    PerTenantAccess = 1 << 0,

    /// <summary>Connecting is a local bookkeeping step (no billable provider call), so the sync may provision the connection record itself.</summary>
    ProvisionsLocally = 1 << 1,

    /// <summary>The connector fabricates demo data; it is not a real bank link and is not throttled like one.</summary>
    SeedsDemoData = 1 << 2,

    /// <summary>The user links institutions inside the app; a sync does nothing until the tenant has linked a connection.</summary>
    UserInitiatedLinking = 1 << 3,

    /// <summary>Balances use the "what you have" sign (a card owing 200 is -200), so liability balances are negated into "owed is positive".</summary>
    ReportsHoldingsSign = 1 << 4,

    /// <summary>Every provider call is billable and budgeted (daily/monthly caps and a kill switch apply).</summary>
    BillablePerCall = 1 << 5,
}
