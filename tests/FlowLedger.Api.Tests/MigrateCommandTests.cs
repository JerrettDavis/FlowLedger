using FluentAssertions;
using Npgsql;

namespace FlowLedger.Api.Tests;

/// <summary>The one-shot <c>--migrate</c> entrypoint used by the compose <c>migrate</c> service (#195).</summary>
[Collection("ApiIntegration")]
public sealed class MigrateCommandTests(FlowLedgerApiFactory factory)
{
    private static string[] Args(string connectionString, string dir) =>
    [
        $"--ConnectionStrings:flowledger={connectionString}",
        $"--Backup:Directory={dir}",
        $"--ObjectStorage:RootPath={dir}",
        "--Database:MigrationLockTimeoutSeconds=20",
        MigrateCommand.Flag, // last: the command-line provider would otherwise take the next argument as this flag's value
    ];

    [Fact]
    public async Task Migrate_applies_the_schema_to_an_empty_database_and_is_idempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fl-migrate-" + Guid.NewGuid().ToString("N"));
        var admin = new NpgsqlConnectionStringBuilder(factory.ConnectionString);
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE migrate_cmd_test", conn);
            await create.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Database = "migrate_cmd_test" }.ConnectionString;

        (await MigrateCommand.RunAsync(Args(target, dir))).Should().Be(0);
        (await MigrateCommand.RunAsync(Args(target, dir))).Should().Be(0, "a second run finds nothing pending");

        await using var check = new NpgsqlConnection(target);
        await check.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", check);
        ((long)(await count.ExecuteScalarAsync())!).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Migrate_exits_non_zero_when_the_database_is_unreachable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fl-migrate-" + Guid.NewGuid().ToString("N"));
        var down = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1";
        (await MigrateCommand.RunAsync(Args(down, dir))).Should().NotBe(0);
    }
}
