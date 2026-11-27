using System.Security.Cryptography;
using System.Text;
using FlowLedger.Application.Features.Users;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.HostStores;

/// <summary>
/// Login (identity) directory through EF (#154). Unique on (issuer, subject), so one login always lands in the same tenant. Several
/// logins can point at one tenant (a person with 3-4 IdP accounts): they are joined by configured owner identity, by verified email,
/// or by redeeming a link code from a signed-in login. The directory spans tenants by design, so it is host-wide (<see cref="HostDb"/>,
/// listed in <see cref="HostWideEntities"/>). Everything here is additive: no statement deletes or rewrites tenant data.
/// </summary>
internal sealed class EfUserDirectory(HostDb host, LegacyTenantClaim legacy) : IUserDirectory
{
    public static readonly TimeSpan LinkCodeLifetime = TimeSpan.FromMinutes(15);
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O/1/I/L
    private const string ProvisionLock = "SELECT pg_advisory_xact_lock(hashtext('flowledger-user-provision'))";

    private volatile bool _householdRegistered;

    private static string[] Split(string? list) =>
        string.IsNullOrWhiteSpace(list)
            ? []
            : list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant())));

    public async Task<ResolvedUser> ResolveAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Serialise provisioning/linking so two simultaneous first logins cannot race.
        await db.Database.ExecuteSqlRawAsync(ProvisionLock, ct);

        var emailTrusted = !string.IsNullOrWhiteSpace(identity.Email) && (identity.EmailVerified || legacy.TrustUnverifiedEmail);

        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.Issuer == identity.Issuer && u.Subject == identity.Subject, ct);
        if (user is not null)
        {
            user.RecordLogin(identity.Email, identity.DisplayName, emailTrusted);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ResolvedUser(user.Id, user.HomeTenantId, user.Email, user.DisplayName, false);
        }

        // 1. Configured household owner: every owner login (subject or verified email) joins the legacy tenant, so the
        //    owner's several IdP accounts all see the same data. Fail closed: with nothing configured nobody matches.
        var subjectMatches = Split(legacy.OwnerSubject).Contains(identity.Subject, StringComparer.Ordinal)
            && (string.IsNullOrWhiteSpace(legacy.OwnerIssuer) || string.Equals(identity.Issuer, legacy.OwnerIssuer.Trim(), StringComparison.Ordinal));
        var emailMatches = identity.EmailVerified && !string.IsNullOrWhiteSpace(identity.Email)
            && (string.IsNullOrWhiteSpace(legacy.OwnerIssuer) || string.Equals(identity.Issuer, legacy.OwnerIssuer.Trim(), StringComparison.Ordinal))
            && Split(legacy.OwnerEmail).Contains(identity.Email!.Trim(), StringComparer.OrdinalIgnoreCase);

        Guid? tenantId = subjectMatches || emailMatches ? legacy.LegacyTenantId : null;

        // 2. Same person, other login: a verified email already held by a verified existing login joins that tenant.
        if (tenantId is null && legacy.AutoLinkVerifiedEmail && emailTrusted)
        {
            // ILIKE with every wildcard escaped is a plain case-insensitive match ("_" is a legal email character).
            var wanted = identity.Email!.Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            tenantId = await db.AppUsers.AsNoTracking()
                .Where(u => u.Email != null && EF.Functions.ILike(u.Email, wanted, "\\") && u.EmailVerified)
                .OrderBy(u => u.CreatedUtc)
                .Select(u => (Guid?)u.HomeTenantId)
                .FirstOrDefaultAsync(ct);
        }

        // 3. Otherwise a brand-new person: a fresh, empty tenant (link it later with a code if it is the same person).
        tenantId ??= Guid.NewGuid();

        var userId = Guid.NewGuid();
        db.AppUsers.Add(AppUserRecord.Create(userId, identity.Issuer, identity.Subject, identity.Email, identity.DisplayName, tenantId.Value, emailTrusted));
        await db.SaveChangesAsync(ct);

        await RegisterTenantAsync(db, tenantId.Value, ct);
        await tx.CommitAsync(ct);
        return new ResolvedUser(userId, tenantId.Value, identity.Email, identity.DisplayName, true);
    }

    public async Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<Guid> { legacy.LegacyTenantId };
        await using var db = await OpenAsync(ct);
        foreach (var id in await db.AppUsers.AsNoTracking().Select(u => u.HomeTenantId).Distinct().ToListAsync(ct))
        {
            ids.Add(id);
        }

        return ids.ToList();
    }

    public async Task<IReadOnlyList<LinkedIdentity>> ListIdentitiesAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.AppUsers.AsNoTracking().Where(u => u.HomeTenantId == tenantId).OrderBy(u => u.CreatedUtc).ToListAsync(ct);
        return rows.Select(u => new LinkedIdentity(u.Id, u.Issuer, u.Subject, u.Email, u.DisplayName, u.LastLoginUtc)).ToList();
    }

    public async Task<LinkCode> CreateLinkCodeAsync(Guid tenantId, CancellationToken ct = default)
    {
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        var code = new string(chars);
        var expires = DateTimeOffset.UtcNow.Add(LinkCodeLifetime);
        await using var db = await OpenAsync(ct);
        db.AppUserLinkCodes.Add(AppUserLinkCodeRecord.Create(HashCode(code), tenantId, expires));
        await db.SaveChangesAsync(ct);
        return new LinkCode($"{code[..5]}-{code[5..]}", expires);
    }

    public async Task<LinkOutcome> RedeemLinkCodeAsync(Guid userId, string code, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(ProvisionLock, ct);

        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return LinkOutcome.UnknownUser;
        }

        // Claim the code. Provisioning, linking and unlinking all hold the advisory lock above, so no other redemption interleaves.
        var hash = HashCode(code ?? string.Empty);
        var now = DateTimeOffset.UtcNow;
        var link = await db.AppUserLinkCodes.SingleOrDefaultAsync(c => c.CodeHash == hash && c.UsedUtc == null && c.ExpiresUtc > now, ct);
        if (link is null)
        {
            return LinkOutcome.InvalidOrExpired;
        }

        link.MarkUsed();
        var current = user.HomeTenantId;
        var target = link.TargetTenantId;
        if (target == current)
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return LinkOutcome.AlreadyLinked;
        }

        // Re-point only this login. The old tenant and all its rows are left intact (nothing is deleted or merged).
        user.HomeTenantId = target;
        await db.SaveChangesAsync(ct);

        // #169: the target is certainly in use; the old tenant is now orphaned when this was its last login (kept visible in the
        // registry so it can be reviewed and offboarded instead of lingering unseen).
        await RegisterTenantAsync(db, target, ct);
        var legacyTenant = legacy.LegacyTenantId;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE tenants SET status = 'orphaned', orphaned_utc = now() WHERE id = {current} AND status = 'active' AND id <> {legacyTenant}
               AND NOT EXISTS (SELECT 1 FROM app_users WHERE tenant_id = {current})", ct);

        await tx.CommitAsync(ct);
        return LinkOutcome.Linked;
    }

    public async Task<bool> UnlinkAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(ProvisionLock, ct);

        // Detached login gets its own fresh tenant; refused when it is the tenant's only login.
        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.Id == userId && u.HomeTenantId == tenantId, ct);
        var fresh = default(Guid?);
        if (user is not null && await db.AppUsers.CountAsync(u => u.HomeTenantId == tenantId, ct) > 1)
        {
            fresh = Guid.NewGuid();
            user.HomeTenantId = fresh.Value;
            await db.SaveChangesAsync(ct);
            await RegisterTenantAsync(db, fresh.Value, ct);
        }

        await tx.CommitAsync(ct);
        return fresh is not null;
    }

    /// <summary>Records the tenant in the registry (or reactivates it when a login lands on a tenant that had been orphaned).</summary>
    private static async Task RegisterTenantAsync(FlowLedgerDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO tenants (id) VALUES ({tenantId}) ON CONFLICT (id) DO UPDATE SET status = 'active', orphaned_utc = NULL
               WHERE tenants.status = 'orphaned'", ct);

    private async Task<FlowLedgerDbContext> OpenAsync(CancellationToken ct)
    {
        var db = host.Create();
        if (!_householdRegistered)
        {
            // The household tenant is always registered, even before anyone has logged in (once per process).
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tenants (id) VALUES ({legacy.LegacyTenantId}) ON CONFLICT (id) DO NOTHING", ct);
            _householdRegistered = true;
        }

        return db;
    }
}
