namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Per-scope tenant for background work that has no HttpContext (the webhook-triggered sync job). Set once, before
/// anything resolves <c>ITenantContext</c>; only consulted when there is no request. Without it a background scope
/// would silently fall back to the household tenant (#150).
/// </summary>
public sealed class TenantOverride
{
    public Guid? TenantId { get; private set; }

    /// <summary>Sets the tenant for this scope. Set-once: a second call (or a change after the tenant was captured) is a bug.</summary>
    public void Use(Guid tenantId)
    {
        if (TenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException("The tenant for this background scope was already set.");
        }

        TenantId = tenantId;
    }
}
