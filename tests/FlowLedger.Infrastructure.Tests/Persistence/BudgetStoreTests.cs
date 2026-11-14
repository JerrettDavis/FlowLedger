using FluentAssertions;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class BudgetStoreTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-bud-" + Guid.NewGuid().ToString("N"));

    public BudgetStoreTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [DockerFact]
    public async Task Budgets_are_per_tenant_upsert_and_removable()
    {
        var store = new NpgsqlBudgetStore(_fixture.ConnectionString);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await store.SetAsync(a, "Food/Groceries", 400m);
        await store.SetAsync(a, "Food/Groceries", 450m);
        await store.SetAsync(b, "Food/Groceries", 100m);

        (await store.ListAsync(a)).Should().ContainSingle().Which.MonthlyLimit.Should().Be(450m);
        (await store.ListAsync(b)).Should().ContainSingle().Which.MonthlyLimit.Should().Be(100m);

        await store.RemoveAsync(a, "Food/Groceries");
        (await store.ListAsync(a)).Should().BeEmpty();
        (await store.ListAsync(b)).Should().HaveCount(1);
    }

    [DockerFact]
    public async Task Budgets_survive_snapshot_and_restore_drill()
    {
        var store = new NpgsqlBudgetStore(_fixture.ConnectionString);
        await store.SetAsync(Guid.NewGuid(), "Housing/Rent", 1500m);

        var (dir, manifest) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "budgets");
        manifest.Tables.Should().Contain(t => t.Table == "budgets" && t.Rows >= 1);

        var drill = await SnapshotRestoreDrill.RunAsync(_fixture.ConnectionString, dir, "flowledger_drill_budgets");

        drill.Ok.Should().BeTrue(string.Join("; ", drill.Problems));
        drill.Tables.Should().Contain(t => t.Table == "budgets" && t.Ok);
    }
}
