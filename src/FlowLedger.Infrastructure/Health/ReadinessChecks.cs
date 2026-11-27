using System.Globalization;
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
internal static class HealthTags
{
    public const string Ready = "ready";
    public const string Live = "live";
}

/// <summary>
/// Readiness: Postgres reachable AND the schema is fully migrated. A reachable database with pending migrations is
/// Unhealthy: the host would serve requests against a schema its code does not match.
/// </summary>
internal sealed class PostgresReadinessCheck(IConfiguration configuration) : IHealthCheck
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
internal sealed class RedisReadinessCheck(IConfiguration configuration) : IHealthCheck
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
        return host.Length > 0 && int.TryParse(endpoint[(colon + 1)..], CultureInfo.InvariantCulture, out port);
    }
}

/// <summary>
/// Reports backup freshness from <c>backup_runs</c>. Never Unhealthy: a stale backup is an operator problem, not a
/// reason to pull the API out of rotation, so it surfaces as Degraded (HTTP 200 on <c>/ready</c>, visible in the body).
/// Window: <c>Health:BackupMaxAgeHours</c> (default 36 h, one missed daily run plus slack). Skipped when
/// <c>Backup:Enabled=false</c>.
/// </summary>
internal sealed class BackupFreshnessCheck(IConfiguration configuration, TimeProvider clock) : IHealthCheck
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

        var maxAge = TimeSpan.FromHours(double.TryParse(configuration["Health:BackupMaxAgeHours"], CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 36);

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

/// <summary>
/// Readiness: the object-storage root (raw provider payload archive) exists or can be created and accepts a write.
/// Unhealthy on failure, because ingest would lose the archive (ADR 0007). Writes and deletes a small probe file.
/// </summary>
internal sealed class ObjectStorageWritableCheck(Microsoft.Extensions.Options.IOptions<FlowLedger.Infrastructure.Storage.ObjectStorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string probe = string.Empty;
        try
        {
            var root = Path.GetFullPath(options.Value.RootPath);
            // A dedicated subdirectory keeps a leftover probe file out of the archive proper.
            var probeDir = Path.Combine(root, ".health");
            Directory.CreateDirectory(probeDir);
            probe = Path.Combine(probeDir, $"probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            return HealthCheckResult.Healthy("Object storage is writable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Object storage root is not writable.", ex);
        }
        finally
        {
            try
            {
                if (probe.Length > 0 && File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup; a leftover probe file is harmless.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// Reports provider sync freshness from <c>sync_cursors</c>. Never Unhealthy (a stale sync is an operator problem, like a stale
/// backup): Degraded when the newest cursor is older than <c>Health:SyncMaxAgeHours</c> (default 48). Healthy when nothing has
/// ever synced (no provider connected yet).
/// </summary>
internal sealed class LastSyncAgeCheck(IConfiguration configuration, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cs = configuration.GetConnectionString("flowledger");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return HealthCheckResult.Degraded("Connection string 'flowledger' is not configured.");
        }

        var maxAge = TimeSpan.FromHours(double.TryParse(configuration["Health:SyncMaxAgeHours"], CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 48);

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(cs) { Timeout = 3, CommandTimeout = 5 };
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var exists = new NpgsqlCommand("SELECT to_regclass('sync_cursors') IS NOT NULL", conn);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                return HealthCheckResult.Healthy("No sync has run yet.");
            }

            await using var cmd = new NpgsqlCommand("SELECT max(last_synced_at) FROM sync_cursors", conn);
            var last = await cmd.ExecuteScalarAsync(cancellationToken);
            DateTimeOffset lastUtc;
            switch (last)
            {
                case DateTimeOffset dto:
                    lastUtc = dto;
                    break;
                case DateTime dt:
                    lastUtc = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                    break;
                default:
                    return HealthCheckResult.Healthy("No sync has run yet.");
            }

            var age = clock.GetUtcNow() - lastUtc;
            var data = new Dictionary<string, object> { ["lastSyncUtc"] = lastUtc, ["ageHours"] = Math.Round(age.TotalHours, 1) };
            return age <= maxAge
                ? HealthCheckResult.Healthy($"Last sync {age.TotalHours:F1} h ago.", data: data)
                : HealthCheckResult.Degraded($"Last sync {age.TotalHours:F1} h ago (limit {maxAge.TotalHours:F0} h).", data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded("Last sync age could not be determined.", ex);
        }
    }
}

public static class ReadinessHealthExtensions
{
    /// <summary>Registers the readiness-tagged checks (Postgres+migrations, Redis if configured, backup freshness).</summary>
    public static IServiceCollection AddFlowLedgerReadinessChecks(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<HealthCacheStore>();
        // /ready and /health are anonymous and each check opens a connection, so results are cached briefly: a flood of probes
        // costs at most one database round trip per check per window (#184 review).
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration("postgres",
                sp => new CachedHealthCheck(ActivatorUtilities.CreateInstance<PostgresReadinessCheck>(sp), CachedHealthCheck.TtlFrom(sp.GetRequiredService<IConfiguration>()), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<HealthCacheStore>().For("postgres")),
                failureStatus: null, tags: [HealthTags.Ready]))
            .Add(new HealthCheckRegistration("redis",
                sp => new CachedHealthCheck(ActivatorUtilities.CreateInstance<RedisReadinessCheck>(sp), CachedHealthCheck.TtlFrom(sp.GetRequiredService<IConfiguration>()), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<HealthCacheStore>().For("redis")),
                failureStatus: null, tags: [HealthTags.Ready]))
            .Add(new HealthCheckRegistration("backup",
                sp => new CachedHealthCheck(ActivatorUtilities.CreateInstance<BackupFreshnessCheck>(sp), CachedHealthCheck.TtlFrom(sp.GetRequiredService<IConfiguration>()), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<HealthCacheStore>().For("backup")),
                failureStatus: null, tags: [HealthTags.Ready]))
            .Add(new HealthCheckRegistration("object-storage",
                sp => new CachedHealthCheck(ActivatorUtilities.CreateInstance<ObjectStorageWritableCheck>(sp), CachedHealthCheck.TtlFrom(sp.GetRequiredService<IConfiguration>()), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<HealthCacheStore>().For("object-storage")),
                failureStatus: null, tags: [HealthTags.Ready]))
            .Add(new HealthCheckRegistration("last-sync",
                sp => new CachedHealthCheck(ActivatorUtilities.CreateInstance<LastSyncAgeCheck>(sp), CachedHealthCheck.TtlFrom(sp.GetRequiredService<IConfiguration>()), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<HealthCacheStore>().For("last-sync")),
                failureStatus: null, tags: [HealthTags.Ready]));
        return services;
    }
}

/// <summary>Shared (singleton) cache state per check name; health check instances are created per request, the cache must outlive them.</summary>
internal sealed class HealthCacheStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HealthCacheState> _states = new();

    public HealthCacheState For(string name) => _states.GetOrAdd(name, _ => new HealthCacheState());
}

internal sealed class HealthCacheState
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal HealthCheckResult Last { get; set; }
    internal DateTimeOffset At { get; set; } = DateTimeOffset.MinValue;
}

/// <summary>Memoises the last result of an inner health check for a short time (includes failures, so an outage is not hammered).</summary>
internal sealed class CachedHealthCheck(IHealthCheck inner, TimeSpan ttl, TimeProvider time, HealthCacheState state) : IHealthCheck
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    /// <summary>Health:CacheSeconds (default 5, 0 disables caching).</summary>
    public static TimeSpan TtlFrom(IConfiguration configuration)
        => int.TryParse(configuration["Health:CacheSeconds"], CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : DefaultTtl;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (time.GetUtcNow() - state.At < ttl)
            {
                return state.Last;
            }

            state.Last = await inner.CheckHealthAsync(context, cancellationToken);
            state.At = time.GetUtcNow();
            return state.Last;
        }
        finally
        {
            state.Gate.Release();
        }
    }
}
