using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#154: budgets moved from an ad-hoc Npgsql table to EF (migration + tenant filter + tenant guard).</summary>
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
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();

        await using (var db = _fixture.CreateProductionStyleContext(a))
        {
            var store = new EfBudgetStore(db, a);
            await store.SetAsync(a.TenantId, "Food/Groceries", 400m);
            await store.SetAsync(a.TenantId, "Food/Groceries", 450m);
        }

        await using (var db = _fixture.CreateProductionStyleContext(b))
        {
            var store = new EfBudgetStore(db, b);
            await store.SetAsync(b.TenantId, "Food/Groceries", 100m);
            (await store.ListAsync(b.TenantId)).Should().ContainSingle().Which.MonthlyLimit.Should().Be(100m);
            await store.RemoveAsync(b.TenantId, "Food/Groceries");
        }

        await using (var db = _fixture.CreateProductionStyleContext(a))
        {
            var store = new EfBudgetStore(db, a);
            (await store.ListAsync(a.TenantId)).Should().ContainSingle().Which.MonthlyLimit.Should().Be(450m,
                "tenant B's delete of the same category must not touch tenant A");
            await store.RemoveAsync(a.TenantId, "Food/Groceries");
            (await store.ListAsync(a.TenantId)).Should().BeEmpty();
        }
    }

    [DockerFact]
    public async Task Asking_for_another_tenants_budgets_fails_loudly()
    {
        var a = TestTenantContext.New();
        await using var db = _fixture.CreateProductionStyleContext(a);
        var store = new EfBudgetStore(db, a);

        var act = () => store.ListAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [DockerFact]
    public async Task Budgets_survive_snapshot_and_restore_drill()
    {
        var a = TestTenantContext.New();
        await using (var db = _fixture.CreateProductionStyleContext(a))
        {
            await new EfBudgetStore(db, a).SetAsync(a.TenantId, "Housing/Rent", 1500m);
        }

        var (dir, manifest) = await new DatabaseSnapshotService(_fixture.ConnectionString).CreateAsync(_root, "budgets");
        manifest.Tables.Should().Contain(t => t.Table == "budgets" && t.Rows >= 1);

        var drill = await SnapshotRestoreDrill.RunAsync(_fixture.ConnectionString, dir, "flowledger_drill_budgets");

        drill.Ok.Should().BeTrue(string.Join("; ", drill.Problems));
        drill.Tables.Should().Contain(t => t.Table == "budgets" && t.Ok);
    }
}
