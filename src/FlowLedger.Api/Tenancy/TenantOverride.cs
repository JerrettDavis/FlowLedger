namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Per-scope tenant for background work that has no HttpContext (the webhook-triggered sync job). Set once, before
/// anything resolves <c>ITenantContext</c>; only consulted when there is no request. Without it a background scope
/// would silently fall back to the household tenant (#150).
/// </summary>
public sealed class TenantOverride
{
    public Guid? TenantId { get; private set; }

    public void Use(Guid tenantId) => TenantId = tenantId;
}
