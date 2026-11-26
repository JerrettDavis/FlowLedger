namespace FlowLedger.Application.Abstractions;

/// <summary>
/// Answers whether the current tenant is the household (operator) tenant: the one that owns instance-wide provider credentials,
/// paid feeds and whole-database administration. Injected where that rule is enforced instead of being dug out of the service
/// provider at call time (#94).
/// </summary>
public interface IHouseholdTenantAccess
{
    /// <summary>True when the current tenant is the household tenant.</summary>
    bool IsHouseholdTenant { get; }
}
