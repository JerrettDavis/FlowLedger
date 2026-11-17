using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class ExportImportRoundTripTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-exp-" + Guid.NewGuid().ToString("N"));

    public ExportImportRoundTripTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        var tenantCtx = TestTenantContext.New();
        await using var ctx = _fixture.CreateDbContext(tenantCtx);
        ctx.Accounts.Add(Account.Create(TenantId.From(tenantCtx.TenantId), "Checking", AccountType.Checking, new Money(5m, Currency.Usd)));
        ctx.Accounts.Add(Account.Create(TenantId.From(tenantCtx.TenantId), "Savings, \"main\"\nx", AccountType.Savings, new Money(7m, Currency.Usd)));
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task<string> CreateEmptyDatabaseAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres", Pooling = false };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    [DockerFact]
    public async Task Export_then_import_into_fresh_database_restores_identical_counts()
    {
        var zip = await SnapshotImport.ExportZipAsync(_fixture.ConnectionString, _root);
        var target = await CreateEmptyDatabaseAsync("flowledger_import_test");

        await using var stream = File.OpenRead(zip);
        var result = await SnapshotImport.ImportZipAsync(target, stream, _root);

        result.Ok.Should().BeTrue(string.Join("; ", result.Problems));
        result.Tables.Single(t => t.Table == "accounts").RestoredRows.Should().Be(2);

        await using var conn = new NpgsqlConnection(target);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM accounts WHERE name LIKE 'Savings%main%'", conn);
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1, "quotes/commas/newlines must survive the round trip");
    }

    [DockerFact]
    public async Task Import_into_a_database_that_has_data_is_refused()
    {
        var zip = await SnapshotImport.ExportZipAsync(_fixture.ConnectionString, _root);

        await using var stream = File.OpenRead(zip);
        var result = await SnapshotImport.ImportZipAsync(_fixture.ConnectionString, stream, _root);

        result.Ok.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Contains("empty database"));
    }
}
