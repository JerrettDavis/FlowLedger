namespace FlowLedger.Integrations.Mx;

/// <summary>
/// MX-specific tunables (NOT credentials — those live in
/// <see cref="FlowLedger.Integrations.Abstractions.FinancialProviderOptions"/> under the
/// <c>Mx</c> section, reused as-is). Bind from the <c>Mx:Provider</c> configuration section.
/// </summary>
public sealed class MxProviderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Mx:Provider";

    /// <summary>
    /// MX <c>records_per_page</c> page size for accounts/transactions. MX allows up to 1000.
    /// </summary>
    public int RecordsPerPage { get; set; } = 100;

    /// <summary>
    /// Cooldown window for manual (user-triggered) refreshes per (tenant, member).
    /// A second manual refresh inside this window is rejected to control aggregation cost.
    /// </summary>
    public TimeSpan ManualRefreshCooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Institution code used when creating a member during a connect flow. In production the
    /// real institution is chosen by the user inside the MX Connect widget; this is the seed
    /// value used to provision the member before the widget hand-off (and by the test fixtures).
    /// </summary>
    public string DefaultInstitutionCode { get; set; } = "mxbank";

    /// <summary>
    /// Optional per-tenant monthly aggregation budget (number of allowed manual refreshes).
    /// Zero or negative means "no budget cap". Reserved for future enforcement.
    /// </summary>
    public int MonthlyManualRefreshBudget { get; set; } = 0;
}
