using System.Runtime.InteropServices;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure;

/// <summary>
/// Household tenant id for NEW multi-user installs (#135). The id used to be a configured constant (default <c>00000000-...-0001</c>), which anyone
/// holding the API key can name. For an install that signs in through OIDC and has no tenant id configured, no accounts and no tenants yet, a random
/// id is generated once, stored in <c>system_settings</c> (<see cref="SettingKey"/>) and from then on surfaced to every reader of
/// <see cref="TenancySettings.Legacy"/> as <c>Tenancy:LegacyTenantId</c>. Legacy installs keep exactly what they configured: any explicit
/// <c>Tenancy:LegacyTenantId</c>/<c>Api:TenantId</c>/<c>Worker:TenantId</c> wins and nothing is generated or read, and an install that already has data
/// but no configured id keeps the well-known constant. Single-household (non-OIDC) installs are untouched: the Web app has no database and forwards its
/// own configured id, so both sides must agree on a configured value there.
/// </summary>
internal static partial class HouseholdTenantBootstrap
{
    public const string SettingKey = "household_tenant_id";

    internal static readonly string[] ExplicitKeys = ["Tenancy:LegacyTenantId", "Api:TenantId", "Worker:TenantId"];

    internal const string ExistingInstallMessage =
        "OIDC sign-in is enabled, no household tenant id is configured and this database already holds data, so the household tenant id would be the " +
        "well-known constant 00000000-0000-0000-0000-000000000001, which anyone holding the API key can name. Set Api__TenantId (compose: API__TENANTID) to the id your " +
        "existing data lives under, or accept the constant explicitly with Tenancy:AllowWellKnownLegacyTenantId=true (compose: TENANCY__ALLOWWELLKNOWNLEGACYTENANTID=true); " +
        "changing the id would hide the existing data. New installs get a random id generated automatically.";

    public static bool HasExplicitId(IConfiguration cfg) =>
        ExplicitKeys.Any(k => Guid.TryParse(cfg[k], out var id) && id != Guid.Empty);

    /// <summary>
    /// Resolves (and for a new OIDC install generates) the household id. <see cref="HouseholdTenantOutcome.ExistingDataNoId"/> is the only
    /// outcome the caller must refuse; an unmigrated database is NOT that (it holds no data yet, the migration job simply has not run).
    /// </summary>
    public static async Task<HouseholdTenantResult> EnsureAsync(
        IConfiguration cfg, string connectionString, bool mayGenerate, HouseholdTenantConfigProvider? sink, ILogger logger, CancellationToken ct)
    {
        if (HasExplicitId(cfg))
        {
            return new HouseholdTenantResult(HouseholdTenantOutcome.Configured, null);
        }

        try
        {
            await using var db = new HostDb(connectionString).Create();
            var stored = await ReadAsync(db, ct);
            var outcome = HouseholdTenantOutcome.Stored;
            if (stored is null)
            {
                var hasData = await db.Accounts.AsNoTracking().AnyAsync(ct) || await db.Tenants.AsNoTracking().AnyAsync(ct);
                if (hasData)
                {
                    return new HouseholdTenantResult(HouseholdTenantOutcome.ExistingDataNoId, null);
                }

                if (!mayGenerate)
                {
                    return new HouseholdTenantResult(HouseholdTenantOutcome.NotApplicable, null);
                }

                var candidate = Guid.NewGuid();
                // First writer wins (API and Worker may start together); everyone then reads back the winner.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO system_settings (key, value) VALUES ({SettingKey}, {candidate.ToString()}) ON CONFLICT (key) DO NOTHING", ct);
                stored = await ReadAsync(db, ct);
                if (stored == candidate)
                {
                    LogGenerated(logger, candidate);
                    outcome = HouseholdTenantOutcome.Generated;
                }
            }

            if (stored is { } id)
            {
                sink?.Publish(id);
                return new HouseholdTenantResult(outcome, id);
            }

            return new HouseholdTenantResult(HouseholdTenantOutcome.NotApplicable, null);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
        {
            // Unmigrated database (a host started before the migration job): no data, nothing stored, nothing to refuse yet.
            return new HouseholdTenantResult(HouseholdTenantOutcome.NotMigrated, null);
        }
    }

    private static async Task<Guid?> ReadAsync(Persistence.FlowLedgerDbContext db, CancellationToken ct)
    {
        var value = await db.SystemSettings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).SingleOrDefaultAsync(ct);
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated a random household tenant id {TenantId} for this new install and stored it in system_settings.")]
    private static partial void LogGenerated(ILogger logger, Guid tenantId);
}

internal enum HouseholdTenantOutcome
{
    /// <summary>An explicit Api:TenantId / Tenancy:LegacyTenantId / Worker:TenantId decides; nothing was read or generated.</summary>
    Configured,

    /// <summary>The id stored in system_settings was published.</summary>
    Stored,

    /// <summary>A new install: a random id was generated, stored and published.</summary>
    Generated,

    /// <summary>The database holds data but no household id (a legacy install under the well-known constant): refuse unless acknowledged.</summary>
    ExistingDataNoId,

    /// <summary>The schema is not there yet (migration job still running): not an error, try again on the next start.</summary>
    NotMigrated,

    /// <summary>Empty database but this host may not generate (not an OIDC install): the configured/default id applies.</summary>
    NotApplicable,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct HouseholdTenantResult(HouseholdTenantOutcome Outcome, Guid? TenantId);

/// <summary>Lowest-effort bridge from the stored id to the synchronous <see cref="IConfiguration"/> readers: a provider added last whose only key is <c>Tenancy:LegacyTenantId</c>.</summary>
internal sealed class HouseholdTenantConfigProvider : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public void Publish(Guid tenantId)
    {
        Data["Tenancy:LegacyTenantId"] = tenantId.ToString();
        OnReload();
    }
}
