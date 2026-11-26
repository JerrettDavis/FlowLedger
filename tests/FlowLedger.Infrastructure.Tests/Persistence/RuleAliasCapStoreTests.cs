using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.SharedKernel;
using FluentAssertions;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class RuleAliasCapStoreTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public RuleAliasCapStoreTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    private readonly List<Guid> _tenants = [];

    private Guid NewTenant()
    {
        var id = Guid.NewGuid();
        _tenants.Add(id);
        return id;
    }

    // Ad-hoc tables are created at runtime and are not in Respawn's reset set, so remove everything these tests wrote;
    // otherwise thousands of leftover rows leak into later tests (e.g. the restore drill).
    public async Task DisposeAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var table in new[] { "category_rules", "merchant_aliases", "budgets", "savings_goals" })
        {
            await using var cmd = new Npgsql.NpgsqlCommand($"DELETE FROM {table} WHERE tenant_id = ANY(@t)", conn);
            cmd.Parameters.AddWithValue("t", _tenants.ToArray());
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // table never created by this test run; nothing to clean
            }
        }
    }

    private sealed class Tenant(Guid id) : ITenantContext
    {
        public Guid TenantId { get; } = id;
        public Guid UserId { get; } = Guid.NewGuid();
    }

    [DockerFact]
    public async Task Rule_store_enforces_the_per_tenant_cap_in_sql_but_still_allows_replacing()
    {
        var store = new NpgsqlCategoryRuleStore(_fixture.ConnectionString, new Tenant(NewTenant()));
        for (var i = 0; i < 1000; i++)
        {
            await store.SetAsync($"P{i}", "Food");
        }

        await store.SetAsync("OVER THE CAP", "Food");
        await store.SetAsync("P5", "Auto");

        var rules = await store.ListAsync();
        rules.Should().HaveCount(1000);
        rules.Should().NotContain(r => r.Pattern == "OVER THE CAP");
        rules.Single(r => r.Pattern == "P5").CategoryPath.Should().Be("Auto");
    }

    [DockerFact]
    public async Task Alias_store_enforces_the_per_tenant_cap_in_sql()
    {
        var store = new NpgsqlMerchantAliasStore(_fixture.ConnectionString, new Tenant(NewTenant()));
        for (var i = 0; i < 1000; i++)
        {
            await store.SetAsync($"P{i}", "A");
        }

        await store.SetAsync("OVER THE CAP", "A");

        (await store.ListAsync()).Should().HaveCount(1000).And.NotContain(a => a.Pattern == "OVER THE CAP");
    }

    [DockerFact]
    public async Task Budget_and_goal_stores_reject_new_rows_over_the_cap_but_allow_updates()
    {
        var tenant = NewTenant();
        await using var db = _fixture.CreateProductionStyleContext(new TestTenantContext(tenant));
        var budgets = new FlowLedger.Infrastructure.Persistence.Repositories.EfBudgetStore(db, new TestTenantContext(tenant));
        for (var i = 0; i < FlowLedger.Infrastructure.Persistence.Repositories.EfBudgetStore.MaxBudgets; i++)
        {
            await budgets.SetAsync(tenant, $"Cat{i}", 10m);
        }

        var overBudget = () => budgets.SetAsync(tenant, "One too many", 10m);
        await overBudget.Should().ThrowAsync<InvalidOperationException>();
        await budgets.SetAsync(tenant, "Cat1", 99m);

        var goals = new FlowLedger.Infrastructure.Persistence.Repositories.EfGoalStore(db, new TestTenantContext(tenant));
        Guid first = default;
        for (var i = 0; i < FlowLedger.Infrastructure.Persistence.Repositories.EfGoalStore.MaxGoals; i++)
        {
            var id = await goals.UpsertAsync(tenant, null, $"G{i}", 100m, 0m, null);
            if (i == 0) { first = id; }
        }

        var overGoal = () => goals.UpsertAsync(tenant, null, "one too many", 100m, 0m, null);
        await overGoal.Should().ThrowAsync<InvalidOperationException>();
        await goals.UpsertAsync(tenant, first, "renamed", 100m, 5m, null);
    }
}
