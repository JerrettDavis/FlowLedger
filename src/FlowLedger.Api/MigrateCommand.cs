using FlowLedger.Infrastructure;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.SharedKernel;

namespace FlowLedger.Api;

/// <summary>
/// <c>dotnet FlowLedger.Api.dll --migrate</c>: applies pending EF Core migrations (under the shared Postgres advisory lock, after a
/// verified pre-migration snapshot, exactly like the auto-migration path) and exits 0, or non-zero on failure. Runs no web server.
/// </summary>
internal static class MigrateCommand
{
    public const string Flag = "--migrate";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddScoped<ITenantContext>(_ => UnscopedTenantContext.Instance);
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddDatabaseMigrationService();

        try
        {
            using var host = builder.Build();
            await host.StartAsync();
            await host.StopAsync();
#pragma warning disable RS0030 // CLI command: its stdout is the result the operator reads, not a log
            Console.WriteLine("Migrations are up to date.");
#pragma warning restore RS0030
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Migration failed: {ex.Message}");
            return 1;
        }
    }
}
