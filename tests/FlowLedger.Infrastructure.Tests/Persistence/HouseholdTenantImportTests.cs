using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// INV-11, host-wide half: the household tenant id belongs to the data. A target that already generated its own id on a first start must
/// take the source's id when a full export is imported, otherwise the imported rows and login mappings sit under an id nothing resolves to.
/// </summary>
[Collection("Integration")]
[Trait("Invariant", "INV-11")]
public sealed class HouseholdTenantImportTests : IAsyncLifetime
{
    private const string Key = "household_tenant_id";

    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-hh-" + Guid.NewGuid().ToString("N"));

    public HouseholdTenantImportTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task SetAsync(string connectionString, string key, string value)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO system_settings (key, value) VALUES (@k, @v) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value", conn);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("v", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> GetAsync(string connectionString, string key)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT value FROM system_settings WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<string> EmptyMigratedTargetAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await create.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        await SnapshotRestoreDrill.MigrateAsync(target, null, CancellationToken.None);
        return target;
    }

    [DockerFact]
    public async Task Import_carries_the_sources_household_tenant_id_over_the_one_the_target_generated()
    {
        var source = Guid.NewGuid().ToString();
        var generatedOnTarget = Guid.NewGuid().ToString();
        await SetAsync(_fixture.ConnectionString, Key, source);
        await SetAsync(_fixture.ConnectionString, "data_environment", "production");
        var zip = await SnapshotImport.ExportZipAsync(_fixture.ConnectionString, _root);

        var target = await EmptyMigratedTargetAsync("flowledger_hh_import");
        await SetAsync(target, Key, generatedOnTarget);
        await SetAsync(target, "data_environment", "sandbox");

        await using var stream = File.OpenRead(zip);
        var result = await SnapshotImport.ImportZipAsync(target, stream, _root);

        result.Ok.Should().BeTrue(string.Join("; ", result.Problems));
        (await GetAsync(target, Key)).Should().Be(source, "the data lives under the source's household id");
        (await GetAsync(target, "data_environment")).Should().Be("sandbox", "every other setting stays the target's own");
    }

    private static Microsoft.Extensions.Configuration.IConfiguration NoConfig() =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

    [DockerFact]
    public async Task An_unmigrated_database_is_not_reported_as_an_install_that_holds_data()
    {
        var admin = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS \"flowledger_hh_unmigrated\" WITH (FORCE)", conn))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await using var create = new NpgsqlCommand("CREATE DATABASE \"flowledger_hh_unmigrated\"", conn);
            await create.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "flowledger_hh_unmigrated", Pooling = false }.ConnectionString;

        var result = await HouseholdTenantBootstrap.EnsureAsync(
            NoConfig(), target, mayGenerate: true, sink: null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        result.Outcome.Should().Be(HouseholdTenantOutcome.NotMigrated, "no schema means no data; the migration job has simply not finished");
    }

    [DockerFact]
    public async Task A_migrated_empty_database_generates_once_and_a_database_with_data_but_no_id_is_refused()
    {
        var target = await EmptyMigratedTargetAsync("flowledger_hh_generate");

        var first = await HouseholdTenantBootstrap.EnsureAsync(
            NoConfig(), target, mayGenerate: true, sink: null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        var second = await HouseholdTenantBootstrap.EnsureAsync(
            NoConfig(), target, mayGenerate: true, sink: null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        first.Outcome.Should().Be(HouseholdTenantOutcome.Generated);
        second.Outcome.Should().Be(HouseholdTenantOutcome.Stored);
        second.TenantId.Should().Be(first.TenantId);

        await using (var conn = new NpgsqlConnection(target))
        {
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand("DELETE FROM system_settings WHERE key = 'household_tenant_id'; INSERT INTO tenants (id) VALUES (gen_random_uuid())", conn);
            await del.ExecuteNonQueryAsync();
        }

        var legacy = await HouseholdTenantBootstrap.EnsureAsync(
            NoConfig(), target, mayGenerate: true, sink: null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        legacy.Outcome.Should().Be(HouseholdTenantOutcome.ExistingDataNoId);
    }

    [DockerFact]
    public async Task Import_of_a_source_without_a_household_id_leaves_the_targets_alone()
    {
        var generatedOnTarget = Guid.NewGuid().ToString();
        var zip = await SnapshotImport.ExportZipAsync(_fixture.ConnectionString, _root);

        var target = await EmptyMigratedTargetAsync("flowledger_hh_import_none");
        await SetAsync(target, Key, generatedOnTarget);

        await using var stream = File.OpenRead(zip);
        var result = await SnapshotImport.ImportZipAsync(target, stream, _root);

        result.Ok.Should().BeTrue(string.Join("; ", result.Problems));
        (await GetAsync(target, Key)).Should().Be(generatedOnTarget);
    }
}
