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
public sealed class DatabaseMigrationService : IHostedService
{
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Applying EF Core migrations (Development mode)...");

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowLedgerDbContext>();

        try
        {
            await SnapshotBeforeMigratingAsync(db, cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("EF Core migrations applied successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply EF Core migrations. Application may not function correctly.");
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

        _logger.LogWarning("{Count} pending migration(s) ({First}...); taking a verified snapshot first.", pending.Count, pending[0]);
        var (dir, manifest) = await new DatabaseSnapshotService(conn)
            .CreateAsync(root, $"pre-migration to {pending[^1]}", "premigrate", ct);
        _logger.LogInformation("Pre-migration snapshot OK -> {Dir} ({Tables} tables, {Rows} rows).",
            dir, manifest.Tables.Count, manifest.Tables.Sum(t => t.Rows));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
