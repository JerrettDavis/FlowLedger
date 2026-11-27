using System.Globalization;
using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>
/// Fails startup with a clear message when the PostgreSQL server is older than the minimum the app supports, instead of
/// surfacing an obscure SQL error mid-migration. PostgreSQL 17 is the version built and tested against
/// (docs/self-hosting/database.md); another supported major only logs a warning.
/// </summary>
internal static class PostgresVersionGuard
{
    public const int MinimumMajor = 16;
    public const int TestedMajor = 17;

    /// <summary>Interprets <c>server_version_num</c> (for example 170004). Throws when unsupported; returns a warning for untested majors.</summary>
    public static string? Evaluate(int serverVersionNum)
    {
        var major = serverVersionNum / 10000;
        if (major < MinimumMajor)
        {
            throw new InvalidOperationException(
                $"Unsupported PostgreSQL server version {major} (server_version_num {serverVersionNum}). " +
                $"FlowLedger requires PostgreSQL {MinimumMajor} or newer and is built and tested against {TestedMajor}. " +
                "Upgrade the database server (see docs/self-hosting/database.md).");
        }

        return major == TestedMajor
            ? null
            : $"PostgreSQL {major} is not the tested version ({TestedMajor}); it may work but is unsupported.";
    }

    public static async Task<int> ReadServerVersionNumAsync(string connectionString, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 10, CommandTimeout = 10 };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SHOW server_version_num", conn);
        return int.Parse((string)(await cmd.ExecuteScalarAsync(ct))!, CultureInfo.InvariantCulture);
    }
}

/// <summary>Runs <see cref="PostgresVersionGuard"/> at host start, before the migration service.</summary>
internal sealed partial class PostgresVersionGuardService(IServiceProvider sp, ILogger<PostgresVersionGuardService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = sp.CreateAsyncScope();
        var cs = FlowLedger.Infrastructure.Persistence.DatabaseDataSource.Resolve(
                scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<FlowLedgerDbContext>().Database)
            ?? throw new InvalidOperationException("No connection string available for the PostgreSQL version check.");

        var versionNum = await PostgresVersionGuard.ReadServerVersionNumAsync(cs, cancellationToken);
        var warning = PostgresVersionGuard.Evaluate(versionNum);
        if (warning is not null)
        {
            LogEvent(logger, warning);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Warning}")]
    private static partial void LogEvent(ILogger logger, string? warning);
}

public static class PostgresVersionGuardExtensions
{
    public static IServiceCollection AddPostgresVersionGuard(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresVersionGuardService>());
        return services;
    }
}
