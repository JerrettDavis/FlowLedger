using FlowLedger.Application.Features.Users;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Users table (created on first use, like the other ad-hoc tables). Unique on (issuer, subject) so the same person
/// from the same IdP always lands in the same tenant; a different IdP/subject is a different user.
/// </summary>
public sealed class NpgsqlUserDirectory(string connectionString, LegacyTenantClaim legacy) : IUserDirectory
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
        """;

    public async Task<ResolvedUser> ResolveAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serialise first-time provisioning so two simultaneous first logins cannot both adopt the legacy tenant.
        await using (var lck = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('flowledger-user-provision'))", conn, tx))
        {
            await lck.ExecuteNonQueryAsync(ct);
        }

        await using (var sel = new NpgsqlCommand(
            "UPDATE app_users SET last_login_utc = now(), email = COALESCE(@e, email), display_name = COALESCE(@n, display_name) " +
            "WHERE issuer = @i AND subject = @s RETURNING id, tenant_id, email, display_name", conn, tx))
        {
            sel.Parameters.AddWithValue("i", identity.Issuer);
            sel.Parameters.AddWithValue("s", identity.Subject);
            sel.Parameters.AddWithValue("e", (object?)identity.Email ?? DBNull.Value);
            sel.Parameters.AddWithValue("n", (object?)identity.DisplayName ?? DBNull.Value);
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

        var legacyTaken = false;
        await using (var chk = new NpgsqlCommand("SELECT 1 FROM app_users WHERE tenant_id = @t LIMIT 1", conn, tx))
        {
            chk.Parameters.AddWithValue("t", legacy.LegacyTenantId);
            legacyTaken = await chk.ExecuteScalarAsync(ct) is not null;
        }

        // Fail closed: adoption needs an explicitly configured owner. An unverified email proves nothing.
        var subjectMatches = !string.IsNullOrWhiteSpace(legacy.OwnerSubject)
            && string.Equals(identity.Subject, legacy.OwnerSubject.Trim(), StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(legacy.OwnerIssuer) || string.Equals(identity.Issuer, legacy.OwnerIssuer.Trim(), StringComparison.Ordinal));
        var emailMatches = !string.IsNullOrWhiteSpace(legacy.OwnerEmail)
            && identity.EmailVerified
            && identity.Email is not null
            && string.Equals(identity.Email.Trim(), legacy.OwnerEmail.Trim(), StringComparison.OrdinalIgnoreCase);
        var tenantId = !legacyTaken && (subjectMatches || emailMatches) ? legacy.LegacyTenantId : Guid.NewGuid();

        var userId = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO app_users (id, issuer, subject, email, display_name, tenant_id) VALUES (@id, @i, @s, @e, @n, @t)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", userId);
            ins.Parameters.AddWithValue("i", identity.Issuer);
            ins.Parameters.AddWithValue("s", identity.Subject);
            ins.Parameters.AddWithValue("e", (object?)identity.Email ?? DBNull.Value);
            ins.Parameters.AddWithValue("n", (object?)identity.DisplayName ?? DBNull.Value);
            ins.Parameters.AddWithValue("t", tenantId);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new ResolvedUser(userId, tenantId, identity.Email, identity.DisplayName, true);
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

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var create = new NpgsqlCommand(CreateSqlText, conn);
        await create.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
