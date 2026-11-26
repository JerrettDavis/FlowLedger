using System.Net.Sockets;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace FlowLedger.Infrastructure.Health;

/// <summary>Tag carried by every check that gates readiness (<c>/ready</c>). Liveness (<c>/alive</c>) uses "live" only.</summary>
public static class HealthTags
{
    public const string Ready = "ready";
    public const string Live = "live";
}

/// <summary>
/// Readiness: Postgres reachable AND the schema is fully migrated. A reachable database with pending migrations is
/// Unhealthy: the host would serve requests against a schema its code does not match.
/// </summary>
public sealed class PostgresReadinessCheck(IConfiguration configuration) : IHealthCheck
{
    private static readonly Lazy<string[]> KnownMigrations = new(() =>
        typeof(PostgresReadinessCheck).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToArray());

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cs = configuration.GetConnectionString("flowledger");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return HealthCheckResult.Unhealthy("Connection string 'flowledger' is not configured.");
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(cs) { Timeout = 3, CommandTimeout = 5 };
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var exists = new NpgsqlCommand("SELECT to_regclass('\"__EFMigrationsHistory\"') IS NOT NULL", conn);
            var hasHistory = (bool)(await exists.ExecuteScalarAsync(cancellationToken))!;

            var applied = new HashSet<string>(StringComparer.Ordinal);
            if (hasHistory)
            {
                await using var cmd = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"", conn);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied.Add(reader.GetString(0));
                }
            }

            var pending = KnownMigrations.Value.Where(m => !applied.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy("Postgres reachable; schema up to date.")
                : HealthCheckResult.Unhealthy($"{pending.Count} pending migration(s); next: {pending[0]}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Postgres is not reachable.", ex);
        }
    }
}

/// <summary>
/// Readiness: Redis reachable, but only when <c>ConnectionStrings:redis</c> is configured (Redis is optional; the
/// in-memory cache is the fallback). A plain TCP connect, no extra client dependency.
/// </summary>
public sealed class RedisReadinessCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cs = configuration.GetConnectionString("redis");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return HealthCheckResult.Healthy("Redis not configured.");
        }

        if (!TryParseEndpoint(cs, out var host, out var port))
        {
            return HealthCheckResult.Unhealthy("ConnectionStrings:redis could not be parsed.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            return HealthCheckResult.Healthy("Redis reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", ex);
        }
    }

    /// <summary>Accepts <c>host:port,opt=...</c> (StackExchange style) and <c>redis://[user:pw@]host[:port]</c>.</summary>
    public static bool TryParseEndpoint(string connectionString, out string host, out int port)
    {
        host = string.Empty;
        port = 6379;
        var cs = connectionString.Trim();

        if (cs.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) || cs.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(cs, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                return false;
            }

            host = uri.Host;
            port = uri.IsDefaultPort ? 6379 : uri.Port;
            return true;
        }

        var endpoint = cs.Split(',')[0].Trim();
        var colon = endpoint.LastIndexOf(':');
        if (colon < 0)
        {
            host = endpoint;
            return host.Length > 0;
        }

        host = endpoint[..colon];
        return host.Length > 0 && int.TryParse(endpoint[(colon + 1)..], out port);
    }
}

/// <summary>
/// Reports backup freshness from <c>backup_runs</c>. Never Unhealthy: a stale backup is an operator problem, not a
/// reason to pull the API out of rotation, so it surfaces as Degraded (HTTP 200 on <c>/ready</c>, visible in the body).
/// Window: <c>Health:BackupMaxAgeHours</c> (default 36 h, one missed daily run plus slack). Skipped when
/// <c>Backup:Enabled=false</c>.
/// </summary>
public sealed class BackupFreshnessCheck(IConfiguration configuration, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (bool.TryParse(configuration["Backup:Enabled"], out var enabled) && !enabled)
        {
            return HealthCheckResult.Healthy("Backups disabled.");
        }

        var cs = configuration.GetConnectionString("flowledger");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return HealthCheckResult.Degraded("Connection string 'flowledger' is not configured.");
        }

        var maxAge = TimeSpan.FromHours(double.TryParse(configuration["Health:BackupMaxAgeHours"], out var h) && h > 0 ? h : 36);

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(cs) { Timeout = 3, CommandTimeout = 5 };
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var exists = new NpgsqlCommand("SELECT to_regclass('backup_runs') IS NOT NULL", conn);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                return HealthCheckResult.Degraded("No backup has ever run (backup_runs does not exist yet).");
            }

            await using var cmd = new NpgsqlCommand("SELECT max(finished_utc) FROM backup_runs WHERE ok", conn);
            var last = await cmd.ExecuteScalarAsync(cancellationToken);
            if (last is not DateTime lastUtc)
            {
                return HealthCheckResult.Degraded("No successful backup recorded.");
            }

            var age = clock.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(lastUtc, DateTimeKind.Utc));
            var data = new Dictionary<string, object> { ["lastSuccessUtc"] = lastUtc, ["ageHours"] = Math.Round(age.TotalHours, 1) };
            return age <= maxAge
                ? HealthCheckResult.Healthy($"Last successful backup {age.TotalHours:F1} h ago.", data: data)
                : HealthCheckResult.Degraded($"Last successful backup {age.TotalHours:F1} h ago (limit {maxAge.TotalHours:F0} h).", data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Database trouble is the Postgres check's verdict; do not double-fail readiness here.
            return HealthCheckResult.Degraded("Backup freshness could not be determined.", ex);
        }
    }
}

public static class ReadinessHealthExtensions
{
    /// <summary>Registers the readiness-tagged checks (Postgres+migrations, Redis if configured, backup freshness).</summary>
    public static IServiceCollection AddFlowLedgerReadinessChecks(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHealthChecks()
            .AddCheck<PostgresReadinessCheck>("postgres", tags: [HealthTags.Ready])
            .AddCheck<RedisReadinessCheck>("redis", tags: [HealthTags.Ready])
            .AddCheck<BackupFreshnessCheck>("backup", tags: [HealthTags.Ready]);
        return services;
    }
}
