using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

public class AdHocSchemaTests
{
    [Fact]
    public void Every_store_with_CreateSqlText_is_registered_for_restore()
    {
        var declared = AdHocSchema.DiscoverDeclared();
        declared.Should().NotBeEmpty();
        foreach (var sql in declared)
        {
            AdHocSchema.Scripts.Should().Contain(sql,
                "tables created outside EF migrations must be recreated by restore/import, otherwise snapshots that include them cannot be restored");
        }
    }
}

[Collection("Integration")]
public sealed class AdHocSchemaRestoreTests : IAsyncLifetime
{
    private readonly FlowLedger.Infrastructure.Tests.Helpers.IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-adhoc-" + Guid.NewGuid().ToString("N"));

    public AdHocSchemaRestoreTests(FlowLedger.Infrastructure.Tests.Helpers.IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [FlowLedger.Infrastructure.Tests.Helpers.DockerFact]
    public async Task Snapshot_with_every_adhoc_table_restores()
    {
        await using (var conn = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand(AdHocSchema.AllSql, conn);
            await create.ExecuteNonQueryAsync();
            await using var ins = new Npgsql.NpgsqlCommand(
                "INSERT INTO merchant_aliases (tenant_id, pattern, alias) VALUES (gen_random_uuid(), 'amzn', 'Amazon')", conn);
            await ins.ExecuteNonQueryAsync();
        }

        var (dir, manifest) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "adhoc");
        foreach (var t in new[] { "merchant_aliases", "recurring_dismissals", "suspicious_dismissals", "savings_goals", "category_rules", "budgets" })
        {
            manifest.Tables.Should().Contain(x => x.Table == t);
        }

        var drill = await SnapshotRestoreDrill.RunAsync(_fixture.ConnectionString, dir, "flowledger_drill_adhoc");

        drill.Ok.Should().BeTrue(string.Join("; ", drill.Problems));
        drill.Tables.Should().Contain(t => t.Table == "merchant_aliases" && t.Ok && t.RestoredRows == 1);
    }
}
