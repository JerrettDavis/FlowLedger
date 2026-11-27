using FlowLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>What kind of data this database holds / this process is configured for.</summary>
public enum DataEnvironment
{
    Demo,
    Sandbox,
    Production,
}

/// <summary>Singleton holding the verified environment of the running process (read by the API + UI).</summary>
public sealed class DataEnvironmentState
{
    public DataEnvironment? Current { get; internal set; }
}

/// <summary>
/// Stamps the database with a durable <c>data_environment</c> marker and refuses to start when the
/// process configuration and the database disagree across the production boundary. This is what
/// stops demo/sandbox rows from ever being mixed into real data (and vice versa).
///
/// Rules (marker = what the DB holds, expected = what this process is configured for):
///  - no marker, DB empty                         -> stamp expected.
///  - no marker, DB has data, expected non-prod   -> stamp expected (legacy dev databases).
///  - no marker, DB has data, expected production -> REFUSE (use a fresh database), unless DataEnvironment:AdoptExistingAsProduction=true.
///  - marker production, expected non-prod (or vice versa) -> REFUSE.
///  - marker and expected both non-prod           -> allowed (demo/sandbox are interchangeable); marker follows expected.
/// The table is created here (IF NOT EXISTS), so no EF migration is involved.
/// </summary>
public static class DataEnvironmentGuard
{
    public const string SettingKey = "data_environment";

    public static DataEnvironment Expected(bool mxEnabled, string? mxEnvironment, bool simpleFinEnabled = false, bool quilttEnabled = false, string? quilttEnvironment = null)
    {
        // SimpleFIN only ever returns real bank data, so it is always PRODUCTION.
        if (simpleFinEnabled)
        {
            return DataEnvironment.Production;
        }

        // Quiltt production is real bank data; Quiltt sandbox is sandbox data.
        if (quilttEnabled && string.Equals(quilttEnvironment, "production", StringComparison.OrdinalIgnoreCase))
        {
            return DataEnvironment.Production;
        }

        if (!mxEnabled)
        {
            return quilttEnabled ? DataEnvironment.Sandbox : DataEnvironment.Demo;
        }

        return string.Equals(mxEnvironment, "production", StringComparison.OrdinalIgnoreCase)
            ? DataEnvironment.Production
            : DataEnvironment.Sandbox;
    }

    public static async Task<DataEnvironment> EnsureAsync(
        string connectionString, DataEnvironment expected, bool adoptExistingAsProduction, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using (var create = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS system_settings (key text PRIMARY KEY, value text NOT NULL, updated_utc timestamptz NOT NULL DEFAULT now())", conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        string? marker;
        await using (var get = new NpgsqlCommand("SELECT value FROM system_settings WHERE key = @k", conn))
        {
            get.Parameters.AddWithValue("k", SettingKey);
            marker = (string?)await get.ExecuteScalarAsync(ct);
        }

        if (marker is null)
        {
            var hasData = false;
            await using (var probe = new NpgsqlCommand(
                "SELECT to_regclass('accounts') IS NOT NULL AND EXISTS (SELECT 1 FROM accounts)", conn))
            {
                hasData = (bool)(await probe.ExecuteScalarAsync(ct))!;
            }

            if (hasData && expected == DataEnvironment.Production && !adoptExistingAsProduction)
            {
                throw new InvalidOperationException(
                    "Refusing to start in PRODUCTION: this database already contains data but has no data_environment marker " +
                    "(it was created in demo/sandbox mode). Point the app at a FRESH database for real data. " +
                    "To deliberately adopt this database as production set DataEnvironment:AdoptExistingAsProduction=true.");
            }

            await WriteAsync(conn, expected, ct);
            return expected;
        }

        if (!Enum.TryParse<DataEnvironment>(marker, ignoreCase: true, out var actual))
        {
            throw new InvalidOperationException($"Unrecognised data_environment marker '{marker}' in the database.");
        }

        if ((actual == DataEnvironment.Production) != (expected == DataEnvironment.Production))
        {
            throw new InvalidOperationException(
                $"Refusing to start: this database is marked '{actual}' but the application is configured for '{expected}'. " +
                "Production and demo/sandbox data must never share a database. Use a separate database (or fix Mx:Enabled / Mx:Environment).");
        }

        if (actual != expected)
        {
            await WriteAsync(conn, expected, ct);
        }

        return expected;
    }

    private static async Task WriteAsync(NpgsqlConnection conn, DataEnvironment env, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO system_settings (key, value) VALUES (@k, @v) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_utc = now()", conn);
        cmd.Parameters.AddWithValue("k", SettingKey);
        cmd.Parameters.AddWithValue("v", env.ToString().ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Runs the guard at startup (register AFTER the migration service so the schema exists).</summary>
internal sealed partial class DataEnvironmentGuardService : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _config;
    private readonly DataEnvironmentState _state;
    private readonly ILogger<DataEnvironmentGuardService> _logger;

    public DataEnvironmentGuardService(
        IServiceProvider sp, IConfiguration config, DataEnvironmentState state, ILogger<DataEnvironmentGuardService> logger)
    {
        _sp = sp;
        _config = config;
        _state = state;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowLedgerDbContext>();
        var conn = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("No connection string available for the data-environment guard.");

        var mxEnabled = bool.TryParse(_config["Mx:Enabled"], out var e) && e;
        var simpleFin = bool.TryParse(_config["SimpleFin:Enabled"], out var s) && s;
        var quiltt = bool.TryParse(_config["Quiltt:Enabled"], out var q) && q;
        var expected = DataEnvironmentGuard.Expected(mxEnabled, _config["Mx:Environment"], simpleFin, quiltt, _config["Quiltt:Environment"]);
        var adopt = bool.TryParse(_config["DataEnvironment:AdoptExistingAsProduction"], out var a) && a;

        _state.Current = await DataEnvironmentGuard.EnsureAsync(conn, expected, adopt, cancellationToken);
        LogDataEnvironmentVerified(_logger, _state.Current);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Data environment verified: {Environment}")]
    private static partial void LogDataEnvironmentVerified(ILogger logger, DataEnvironment? environment);
}

public static class DataEnvironmentExtensions
{
    /// <summary>Register after AddDatabaseMigrationService so migrations run first.</summary>
    public static IServiceCollection AddDataEnvironmentGuard(this IServiceCollection services)
    {
        services.AddSingleton<DataEnvironmentState>();
        services.AddHostedService<DataEnvironmentGuardService>();
        return services;
    }
}
