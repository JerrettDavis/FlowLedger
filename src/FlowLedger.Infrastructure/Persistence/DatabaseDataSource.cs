using System.Reflection;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FlowLedger.Infrastructure.Persistence;

/// <summary>
/// Connection pool policy for the one <see cref="NpgsqlDataSource"/> every EF context shares (#180). Bound from the <c>Database</c>
/// configuration section. A value the connection string already sets (<c>Maximum Pool Size=</c>, <c>Timeout=</c>, ...) always wins,
/// so an operator can still tune everything with one string.
/// </summary>
public sealed class DatabaseConnectionOptions
{
    public const string SectionName = "Database";

    /// <summary>Hard ceiling on connections one process holds open; excess requests queue for <see cref="ConnectionTimeoutSeconds"/>.</summary>
    public int MaxPoolSize { get; set; } = 40;

    /// <summary>Seconds to wait to open a connection (or to get one from a saturated pool) before failing.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 15;

    /// <summary>Seconds a single command may run before it is cancelled.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Overrides the default <c>Application Name</c> (the entry assembly, so api and worker show up separately in pg_stat_activity).</summary>
    public string? ApplicationName { get; set; }

    public static DatabaseConnectionOptions From(IConfiguration configuration)
    {
        var options = new DatabaseConnectionOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }
}

/// <summary>Builds the shared, bounded, named <see cref="NpgsqlDataSource"/> (#180).</summary>
public static class DatabaseDataSource
{
    public static string Normalize(string connectionString, DatabaseConnectionOptions options)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.ShouldSerialize("Maximum Pool Size"))
        {
            builder.MaxPoolSize = Math.Max(1, options.MaxPoolSize);
        }

        if (!builder.ShouldSerialize("Timeout"))
        {
            builder.Timeout = Math.Max(1, options.ConnectionTimeoutSeconds);
        }

        if (!builder.ShouldSerialize("Command Timeout"))
        {
            builder.CommandTimeout = Math.Max(1, options.CommandTimeoutSeconds);
        }

        if (!builder.ShouldSerialize("Application Name"))
        {
            var name = options.ApplicationName ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "FlowLedger";
            builder.ApplicationName = name.Length > 63 ? name[..63] : name; // Postgres truncates at 63 bytes
        }

        return builder.ConnectionString;
    }

    public static NpgsqlDataSource Create(string connectionString, DatabaseConnectionOptions options)
        => new NpgsqlDataSourceBuilder(Normalize(connectionString, options)).Build();
}
