using Microsoft.EntityFrameworkCore;
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

    public DatabaseMigrationService(
        IServiceProvider serviceProvider,
        ILogger<DatabaseMigrationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Applying EF Core migrations (Development mode)...");

        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowLedgerDbContext>();

        try
        {
            await db.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("EF Core migrations applied successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply EF Core migrations. Application may not function correctly.");
            throw;
        }
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
