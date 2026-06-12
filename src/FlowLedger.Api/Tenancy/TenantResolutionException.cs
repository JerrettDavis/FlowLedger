namespace FlowLedger.Api.Tenancy;

/// <summary>
/// Thrown by <see cref="HeaderTenantContext"/> when the X-Tenant-Id header is absent
/// or malformed in a non-Development environment.
/// Caught by the ProblemDetails exception handler and mapped to HTTP 401.
/// </summary>
public sealed class TenantResolutionException : Exception
{
    public TenantResolutionException()
        : base("A valid X-Tenant-Id header is required.") { }
}
