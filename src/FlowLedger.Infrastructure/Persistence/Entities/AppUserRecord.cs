namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>
/// One login (identity) in the user directory (#154). The directory resolves a login to its tenant, so it spans tenants by design:
/// it is host-wide, carries no query filter and is reached only through <see cref="HostDb"/>. <see cref="HomeTenantId"/> (column
/// <c>tenant_id</c>) is deliberately not called TenantId so no tenant filter or erasure catalog mistakes this for tenant-owned data.
/// </summary>
internal sealed class AppUserRecord
{
    public Guid Id { get; private set; }
    public string Issuer { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    public Guid HomeTenantId { get; set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset LastLoginUtc { get; private set; }
    public bool EmailVerified { get; private set; }

    private AppUserRecord() { }

    public static AppUserRecord Create(Guid id, string issuer, string subject, string? email, string? displayName, Guid tenantId, bool emailVerified) =>
        new()
        {
            Id = id,
            Issuer = issuer,
            Subject = subject,
            Email = email,
            DisplayName = displayName,
            HomeTenantId = tenantId,
            EmailVerified = emailVerified,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastLoginUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>A returning login: refreshes the profile (a missing value keeps the stored one) and the email-verified flag only when an email was supplied.</summary>
    public void RecordLogin(string? email, string? displayName, bool emailTrusted)
    {
        LastLoginUtc = DateTimeOffset.UtcNow;
        if (email is not null)
        {
            Email = email;
            EmailVerified = emailTrusted;
        }

        DisplayName = displayName ?? DisplayName;
    }
}
