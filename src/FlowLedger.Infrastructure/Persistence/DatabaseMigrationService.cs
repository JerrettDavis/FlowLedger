using FlowLedger.Infrastructure.DataSafety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Hosted service that runs EF Core migrations automatically at startup
/// when the application is running in Development environment.
///
/// Production deployments should apply migrations via a dedicated migration
/// job or CI step, NOT through this service (guarded by IsDevelopment check).
///
/// This is registered via AddDatabaseMigrationService() — call that from the
/// API/Worker host startup only in Development.
/// </summary>
internal sealed partial class DatabaseMigrationService : IHostedService
{
    /// <summary>Advisory-lock name shared by every process that may apply migrations.</summary>

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabaseMigrationService> _logger;
    private readonly IConfiguration _config;

    public DatabaseMigrationService(
        IServiceProvider serviceProvider,
        ILogger<DatabaseMigrationService> logger,
        IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    /// <summary>Cross-process lock name: serialises concurrent starters (API replicas, rolling deploys) on one database.</summary>
    public const string LockName = "flowledger:migration";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowLedgerDbContext>();

        try
        {
            // One migrator at a time: the snapshot-then-migrate sequence is not safe to interleave, and a second
            // starter must see the finished schema rather than race it. The lock is session-scoped, so a crashed
            // starter releases it automatically.
            var conn = db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("No connection string available for migrations.");
            var waitSeconds = int.TryParse(_config["Database:MigrationLockTimeoutSeconds"], out var w) && w > 0 ? w : 300;
            await using var migrationLock = await PostgresAdvisoryLock.AcquireAsync(conn, LockName, TimeSpan.FromSeconds(waitSeconds), cancellationToken);

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count == 0)
            {
                LogEFCoreSchemaIsUp(_logger);
                return;
            }

            LogPendingMigrationSToApply(_logger, pending.Count, pending[^1]);
            await SnapshotBeforeMigratingAsync(db, cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
            LogEFCoreMigrationsAppliedSuccessfully(_logger);
        }
        catch (Exception ex)
        {
            LogFailedToApplyEFCore(_logger, ex);
            throw;
        }
    }

    /// <summary>
    /// Data-safety guard: when migrations are pending against a database that already has data, take and
    /// VERIFY a snapshot first. If that fails, startup aborts and nothing is migrated.
    /// Disable only with Backup:PreMigrationSnapshot=false (never do this against real data).
    /// </summary>
    private async Task SnapshotBeforeMigratingAsync(FlowLedgerDbContext db, CancellationToken ct)
    {
        if (bool.TryParse(_config["Backup:PreMigrationSnapshot"], out var enabled) && !enabled)
        {
            return;
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0 || applied.Count == 0)
        {
            return; // nothing to migrate, or brand-new database
        }

        var conn = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("No connection string available for the pre-migration snapshot.");
        var root = _config["Backup:Directory"] ?? DatabaseSnapshotService.DefaultBackupRoot();

        LogPendingMigrationSTakingA(_logger, pending.Count, pending[0]);
        var (dir, manifest) = await new DatabaseSnapshotService(conn)
            .CreateAsync(root, $"pre-migration to {pending[^1]}", "premigrate", ct);
        var totalRows = manifest.Tables.Sum(t => t.Rows);
        LogPreMigrationSnapshotOKTables(_logger, dir, manifest.Tables.Count, totalRows);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "EF Core schema is up to date; nothing to migrate.")]
    private static partial void LogEFCoreSchemaIsUp(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} pending migration(s) to apply (last: {Last}).")]
    private static partial void LogPendingMigrationSToApply(ILogger logger, int count, string last);

    [LoggerMessage(Level = LogLevel.Information, Message = "EF Core migrations applied successfully.")]
    private static partial void LogEFCoreMigrationsAppliedSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to apply EF Core migrations. Application may not function correctly.")]
    private static partial void LogFailedToApplyEFCore(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} pending migration(s) ({First}...); taking a verified snapshot first.")]
    private static partial void LogPendingMigrationSTakingA(ILogger logger, int count, string first);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pre-migration snapshot OK -> {Dir} ({Tables} tables, {Rows} rows).")]
    private static partial void LogPreMigrationSnapshotOKTables(ILogger logger, string? dir, int tables, long rows);
}

public static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Registers the <see cref="DatabaseMigrationService"/> as a hosted service.
    /// Call this ONLY in Development environments — it auto-migrates on startup.
    /// </summary>
    public static IServiceCollection AddDatabaseMigrationService(this IServiceCollection services)
    {
        services.AddHostedService<DatabaseMigrationService>();
        return services;
    }
}
