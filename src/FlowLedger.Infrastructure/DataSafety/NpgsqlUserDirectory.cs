using System.Security.Cryptography;
using System.Text;
using FlowLedger.Application.Features.Users;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Login (identity) directory, created on first use like the other ad-hoc tables. Unique on (issuer, subject), so one
/// login always lands in the same tenant. Several logins can point at one tenant (a person with 3-4 IdP accounts):
/// they are joined by configured owner identity, by verified email, or by redeeming a link code from a signed-in login.
/// Everything here is additive: no statement deletes or rewrites tenant data.
/// </summary>
internal sealed class NpgsqlUserDirectory(string connectionString, LegacyTenantClaim legacy) : IUserDirectory
{
    public const string CreateSqlText = """
        CREATE TABLE IF NOT EXISTS app_users (
            id uuid PRIMARY KEY,
            issuer text NOT NULL,
            subject text NOT NULL,
            email text NULL,
            display_name text NULL,
            tenant_id uuid NOT NULL,
            created_utc timestamptz NOT NULL DEFAULT now(),
            last_login_utc timestamptz NOT NULL DEFAULT now(),
            UNIQUE (issuer, subject)
        );
        ALTER TABLE app_users ADD COLUMN IF NOT EXISTS email_verified boolean NOT NULL DEFAULT false;
        CREATE INDEX IF NOT EXISTS ix_app_users_tenant ON app_users (tenant_id);
        CREATE INDEX IF NOT EXISTS ix_app_users_email ON app_users (lower(email));
        CREATE TABLE IF NOT EXISTS app_user_link_codes (
            code_hash text PRIMARY KEY,
            tenant_id uuid NOT NULL,
            created_utc timestamptz NOT NULL DEFAULT now(),
            expires_utc timestamptz NOT NULL,
            used_utc timestamptz NULL
        );
        """;

    public static readonly TimeSpan LinkCodeLifetime = TimeSpan.FromMinutes(15);
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O/1/I/L
    private const string ProvisionLock = "SELECT pg_advisory_xact_lock(hashtext('flowledger-user-provision'))";

    private volatile bool _schemaReady;

    private static string[] Split(string? list) =>
        string.IsNullOrWhiteSpace(list)
            ? []
            : list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant())));

    public async Task<ResolvedUser> ResolveAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialise provisioning/linking so two simultaneous first logins cannot race.
        await using (var lck = new NpgsqlCommand(ProvisionLock, conn, tx))
        {
            await lck.ExecuteNonQueryAsync(ct);
        }

        var emailTrusted = !string.IsNullOrWhiteSpace(identity.Email) && (identity.EmailVerified || legacy.TrustUnverifiedEmail);

        await using (var sel = new NpgsqlCommand(
            "UPDATE app_users SET last_login_utc = now(), email = COALESCE(@e, email), display_name = COALESCE(@n, display_name), " +
            "email_verified = CASE WHEN @e IS NULL THEN email_verified ELSE @v END " +
            "WHERE issuer = @i AND subject = @s RETURNING id, tenant_id, email, display_name", conn, tx))
        {
            sel.Parameters.AddWithValue("i", identity.Issuer);
            sel.Parameters.AddWithValue("s", identity.Subject);
            sel.Parameters.AddWithValue("e", (object?)identity.Email ?? DBNull.Value);
            sel.Parameters.AddWithValue("n", (object?)identity.DisplayName ?? DBNull.Value);
            sel.Parameters.AddWithValue("v", emailTrusted);
            await using var r = await sel.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var existing = new ResolvedUser(r.GetGuid(0), r.GetGuid(1),
                    r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), false);
                await r.CloseAsync();
                await tx.CommitAsync(ct);
                return existing;
            }
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
            await using var byEmail = new NpgsqlCommand(
                "SELECT tenant_id FROM app_users WHERE lower(email) = lower(@e) AND email_verified ORDER BY created_utc LIMIT 1", conn, tx);
            byEmail.Parameters.AddWithValue("e", identity.Email!.Trim());
            if (await byEmail.ExecuteScalarAsync(ct) is Guid linked)
            {
                tenantId = linked;
            }
        }

        // 3. Otherwise a brand-new person: a fresh, empty tenant (link it later with a code if it is the same person).
        tenantId ??= Guid.NewGuid();

        var userId = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO app_users (id, issuer, subject, email, display_name, tenant_id, email_verified) VALUES (@id, @i, @s, @e, @n, @t, @v)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", userId);
            ins.Parameters.AddWithValue("i", identity.Issuer);
            ins.Parameters.AddWithValue("s", identity.Subject);
            ins.Parameters.AddWithValue("e", (object?)identity.Email ?? DBNull.Value);
            ins.Parameters.AddWithValue("n", (object?)identity.DisplayName ?? DBNull.Value);
            ins.Parameters.AddWithValue("t", tenantId.Value);
            ins.Parameters.AddWithValue("v", emailTrusted);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await RegisterTenantAsync(conn, tx, tenantId.Value, ct);
        await tx.CommitAsync(ct);
        return new ResolvedUser(userId, tenantId.Value, identity.Email, identity.DisplayName, true);
    }

    public async Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<Guid> { legacy.LegacyTenantId };
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT DISTINCT tenant_id FROM app_users", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            ids.Add(r.GetGuid(0));
        }

        return ids.ToList();
    }

    public async Task<IReadOnlyList<LinkedIdentity>> ListIdentitiesAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, issuer, subject, email, display_name, last_login_utc FROM app_users WHERE tenant_id = @t ORDER BY created_utc", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        var list = new List<LinkedIdentity>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new LinkedIdentity(r.GetGuid(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetFieldValue<DateTimeOffset>(5)));
        }

        return list;
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
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO app_user_link_codes (code_hash, tenant_id, expires_utc) VALUES (@h, @t, @x)", conn);
        cmd.Parameters.AddWithValue("h", HashCode(code));
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("x", expires);
        await cmd.ExecuteNonQueryAsync(ct);
        return new LinkCode($"{code[..5]}-{code[5..]}", expires);
    }

    public async Task<LinkOutcome> RedeemLinkCodeAsync(Guid userId, string code, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lck = new NpgsqlCommand(ProvisionLock, conn, tx))
        {
            await lck.ExecuteNonQueryAsync(ct);
        }

        Guid? current;
        await using (var u = new NpgsqlCommand("SELECT tenant_id FROM app_users WHERE id = @u", conn, tx))
        {
            u.Parameters.AddWithValue("u", userId);
            current = await u.ExecuteScalarAsync(ct) as Guid?;
        }

        if (current is null)
        {
            return LinkOutcome.UnknownUser;
        }

        Guid target;
        await using (var c = new NpgsqlCommand(
            "UPDATE app_user_link_codes SET used_utc = now() WHERE code_hash = @h AND used_utc IS NULL AND expires_utc > now() RETURNING tenant_id", conn, tx))
        {
            c.Parameters.AddWithValue("h", HashCode(code ?? string.Empty));
            if (await c.ExecuteScalarAsync(ct) is not Guid t)
            {
                return LinkOutcome.InvalidOrExpired;
            }

            target = t;
        }

        if (target == current)
        {
            await tx.CommitAsync(ct);
            return LinkOutcome.AlreadyLinked;
        }

        // Re-point only this login. The old tenant and all its rows are left intact (nothing is deleted or merged).
        await using (var m = new NpgsqlCommand("UPDATE app_users SET tenant_id = @t WHERE id = @u", conn, tx))
        {
            m.Parameters.AddWithValue("t", target);
            m.Parameters.AddWithValue("u", userId);
            await m.ExecuteNonQueryAsync(ct);
        }

        // #169: the target is certainly in use; the old tenant is now orphaned when this was its last login (kept visible in the
        // registry so it can be reviewed and offboarded instead of lingering unseen).
        await RegisterTenantAsync(conn, tx, target, ct);
        await using (var o = new NpgsqlCommand(
            "UPDATE tenants SET status = 'orphaned', orphaned_utc = now() WHERE id = @old AND status = 'active' AND id <> @legacy " +
            "AND NOT EXISTS (SELECT 1 FROM app_users WHERE tenant_id = @old)", conn, tx))
        {
            o.Parameters.AddWithValue("old", current.Value);
            o.Parameters.AddWithValue("legacy", legacy.LegacyTenantId);
            await o.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return LinkOutcome.Linked;
    }

    public async Task<bool> UnlinkAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lck = new NpgsqlCommand(ProvisionLock, conn, tx))
        {
            await lck.ExecuteNonQueryAsync(ct);
        }

        // Detached login gets its own fresh tenant; refused when it is the tenant's only login.
        await using var cmd = new NpgsqlCommand(
            "UPDATE app_users SET tenant_id = gen_random_uuid() WHERE id = @u AND tenant_id = @t " +
            "AND (SELECT count(*) FROM app_users WHERE tenant_id = @t) > 1 RETURNING tenant_id", conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("t", tenantId);
        var fresh = await cmd.ExecuteScalarAsync(ct) as Guid?;
        if (fresh is { } id)
        {
            await RegisterTenantAsync(conn, tx, id, ct);
        }

        await tx.CommitAsync(ct);
        return fresh is not null;
    }

    /// <summary>Records the tenant in the registry (or reactivates it when a login lands on a tenant that had been orphaned).</summary>
    private static async Task RegisterTenantAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO tenants (id) VALUES (@t) ON CONFLICT (id) DO UPDATE SET status = 'active', orphaned_utc = NULL " +
            "WHERE tenants.status = 'orphaned'", conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        if (!_schemaReady)
        {
            // ALTER ... IF NOT EXISTS still takes a table lock, so run the idempotent DDL once per process.
            await using var create = new NpgsqlCommand(string.Join(" ", CreateSqlText, NpgsqlTenantRegistry.CreateSqlText, NpgsqlTenantRegistry.BackfillSqlText), conn);
            await create.ExecuteNonQueryAsync(ct);
            await using var household = new NpgsqlCommand("INSERT INTO tenants (id) VALUES (@t) ON CONFLICT (id) DO NOTHING", conn);
            household.Parameters.AddWithValue("t", legacy.LegacyTenantId);
            await household.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }

        return conn;
    }
}
