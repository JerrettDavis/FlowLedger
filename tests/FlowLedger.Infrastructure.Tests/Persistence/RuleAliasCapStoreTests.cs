using FlowLedger.Infrastructure.Tests.Helpers;
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

    [DockerFact]
    public async Task Rule_store_enforces_the_per_tenant_cap_in_sql_but_still_allows_replacing()
    {
        var ctx = new TestTenantContext(NewTenant());
        await using var db = _fixture.CreateProductionStyleContext(ctx);
        var store = new FlowLedger.Infrastructure.Persistence.Repositories.EfCategoryRuleStore(db, ctx);
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
        var ctx = new TestTenantContext(NewTenant());
        await using var db = _fixture.CreateProductionStyleContext(ctx);
        var store = new FlowLedger.Infrastructure.Persistence.Repositories.EfMerchantAliasStore(db, ctx);
        for (var i = 0; i < 1000; i++)
        {
            await store.SetAsync($"P{i}", "A");
        }

        await store.SetAsync("OVER THE CAP", "A");

        (await store.ListAsync()).Should().HaveCount(1000).And.NotContain(a => a.Pattern == "OVER THE CAP");
    }

    [DockerFact]
    public async Task Budget_and_goal_handlers_reject_new_rows_over_the_cap_but_allow_updates()
    {
        var tenant = NewTenant();
        var tenantContext = new TestTenantContext(tenant);
        await using var db = _fixture.CreateProductionStyleContext(tenantContext);
        var budgets = new FlowLedger.Application.Features.Budgets.SetBudgetHandler(
            new FlowLedger.Infrastructure.Persistence.Repositories.EfBudgetStore(db, tenantContext), tenantContext);
        for (var i = 0; i < FlowLedger.Application.Features.Budgets.BudgetLimits.MaxBudgets; i++)
        {
            await budgets.HandleAsync($"Cat{i}", 10m);
        }

        var overBudget = () => budgets.HandleAsync("One too many", 10m);
        await overBudget.Should().ThrowAsync<FlowLedger.Application.Abstractions.RequestValidationException>();
        await budgets.HandleAsync("Cat1", 99m);

        var goals = new FlowLedger.Application.Features.Goals.SaveGoalHandler(
            new FlowLedger.Infrastructure.Persistence.Repositories.EfGoalStore(db, tenantContext), tenantContext);
        Guid first = default;
        for (var i = 0; i < FlowLedger.Application.Features.Goals.GoalLimits.MaxGoals; i++)
        {
            var (id, _) = await goals.HandleAsync(new FlowLedger.Application.Features.Goals.SaveGoalRequest(null, $"G{i}", 100m, 0m, null));
            if (i == 0) { first = id; }
        }

        var overGoal = () => goals.HandleAsync(new FlowLedger.Application.Features.Goals.SaveGoalRequest(null, "one too many", 100m, 0m, null));
        await overGoal.Should().ThrowAsync<FlowLedger.Application.Abstractions.RequestValidationException>();
        await goals.HandleAsync(new FlowLedger.Application.Features.Goals.SaveGoalRequest(first, "renamed", 100m, 5m, null));
    }
}
