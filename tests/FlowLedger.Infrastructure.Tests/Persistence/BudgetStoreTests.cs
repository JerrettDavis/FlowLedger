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

    [DockerFact]
    public async Task Rollover_and_the_carried_amount_are_stored_and_reset_when_the_limit_or_the_flag_changes()
    {
        var a = TestTenantContext.New();
        await using var db = _fixture.CreateProductionStyleContext(a);
        var store = new EfBudgetStore(db, a);

        await store.SetAsync(a.TenantId, "Food", 100m, rollover: true);
        var carriedVersion = await store.SetCarriedAsync(a.TenantId, "Food", 60m, "2026-11");
        var row = (await store.ListAsync(a.TenantId)).Single();
        row.Rollover.Should().BeTrue();
        row.CarriedAmount.Should().Be(60m);
        row.CarriedForMonth.Should().Be("2026-11");
        row.Version.Should().Be(carriedVersion);

        await store.SetAsync(a.TenantId, "Food", 100m); // same limit, rollover untouched: the carry stays
        (await store.ListAsync(a.TenantId)).Single().CarriedAmount.Should().Be(60m);

        await store.SetAsync(a.TenantId, "Food", 120m); // the carry was worked out against the old limit
        row = (await store.ListAsync(a.TenantId)).Single();
        row.Rollover.Should().BeTrue();
        row.CarriedForMonth.Should().BeNull();
        row.CarriedAmount.Should().Be(0m);

        await store.SetCarriedAsync(a.TenantId, "Food", 20m, "2026-11");
        await store.SetAsync(a.TenantId, "Food", 120m, rollover: false);
        row = (await store.ListAsync(a.TenantId)).Single();
        row.Rollover.Should().BeFalse();
        row.CarriedAmount.Should().Be(0m);

        (await store.SetCarriedAsync(a.TenantId, "Missing", 5m, "2026-11")).Should().Be(0u);
    }

    [DockerFact]
    public async Task A_stale_budget_write_or_delete_is_rejected_and_a_current_one_applies()
    {
        var a = TestTenantContext.New();
        await using var seed = _fixture.CreateProductionStyleContext(a);
        var v1 = await new EfBudgetStore(seed, a).SetAsync(a.TenantId, "Fun", 50m);

        await using var winner = _fixture.CreateProductionStyleContext(a);
        var v2 = await new EfBudgetStore(winner, a).SetAsync(a.TenantId, "Fun", 60m, v1);
        v2.Should().NotBe(v1);

        await using var loser = _fixture.CreateProductionStyleContext(a);
        var store = new EfBudgetStore(loser, a);
        var staleSet = () => store.SetAsync(a.TenantId, "Fun", 70m, v1);
        await staleSet.Should().ThrowAsync<FlowLedger.Application.Abstractions.ConcurrencyConflictException>();
        loser.ChangeTracker.Clear();
        var staleDelete = () => store.RemoveAsync(a.TenantId, "Fun", v1);
        await staleDelete.Should().ThrowAsync<FlowLedger.Application.Abstractions.ConcurrencyConflictException>();

        await using var check = _fixture.CreateProductionStyleContext(a);
        var row = (await new EfBudgetStore(check, a).ListAsync(a.TenantId)).Should().ContainSingle().Subject;
        row.MonthlyLimit.Should().Be(60m);
        row.Version.Should().Be(v2);

        await new EfBudgetStore(check, a).RemoveAsync(a.TenantId, "Fun", v2);
        (await new EfBudgetStore(check, a).ListAsync(a.TenantId)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_versioned_budget_write_for_a_row_removed_elsewhere_is_a_conflict_and_an_unversioned_one_upserts()
    {
        var a = TestTenantContext.New();
        await using var db = _fixture.CreateProductionStyleContext(a);
        var store = new EfBudgetStore(db, a);

        var gone = () => store.SetAsync(a.TenantId, "Missing", 10m, expectedVersion: 1);
        await gone.Should().ThrowAsync<FlowLedger.Application.Abstractions.ConcurrencyConflictException>();

        await store.SetAsync(a.TenantId, "Missing", 10m);
        (await store.ListAsync(a.TenantId)).Should().ContainSingle();
    }

    [DockerFact]
    public async Task A_stale_goal_write_or_delete_is_rejected_and_a_current_one_applies()
    {
        var a = TestTenantContext.New();
        await using var seed = _fixture.CreateProductionStyleContext(a);
        var (id, v1) = await new EfGoalStore(seed, a).UpsertAsync(a.TenantId, null, "Car", 1000m, 0m, null);

        await using var winner = _fixture.CreateProductionStyleContext(a);
        var (_, v2) = await new EfGoalStore(winner, a).UpsertAsync(a.TenantId, id, "Car", 1000m, 100m, null, expectedVersion: v1);
        v2.Should().NotBe(v1);

        await using var loser = _fixture.CreateProductionStyleContext(a);
        var store = new EfGoalStore(loser, a);
        var staleUpsert = () => store.UpsertAsync(a.TenantId, id, "Car", 1000m, 5m, null, expectedVersion: v1);
        await staleUpsert.Should().ThrowAsync<FlowLedger.Application.Abstractions.ConcurrencyConflictException>();
        loser.ChangeTracker.Clear();
        var staleDelete = () => store.RemoveAsync(a.TenantId, id, v1);
        await staleDelete.Should().ThrowAsync<FlowLedger.Application.Abstractions.ConcurrencyConflictException>();

        await using var check = _fixture.CreateProductionStyleContext(a);
        var row = (await new EfGoalStore(check, a).ListAsync(a.TenantId)).Should().ContainSingle().Subject;
        row.SavedAmount.Should().Be(100m);
        row.Version.Should().Be(v2);
    }

    [DockerFact]
    public async Task One_tenant_cannot_version_check_or_touch_another_tenants_goal()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        await using var dbA = _fixture.CreateProductionStyleContext(a);
        var (id, v) = await new EfGoalStore(dbA, a).UpsertAsync(a.TenantId, null, "Mine", 10m, 0m, null);

        await using var dbB = _fixture.CreateProductionStyleContext(b);
        var storeB = new EfGoalStore(dbB, b);
        await storeB.RemoveAsync(b.TenantId, id, v); // not B's row: invisible, so a no-op

        await using var check = _fixture.CreateProductionStyleContext(a);
        (await new EfGoalStore(check, a).ListAsync(a.TenantId)).Should().ContainSingle();
    }
}
