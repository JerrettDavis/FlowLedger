using FlowLedger.Application.Features.Categories;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#154: the category move/merge store went from a raw Npgsql connection to the tenant-scoped EF context.</summary>
[Collection("Integration")]
public sealed class EfCategoryMaintenanceStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(Guid Dining, Guid Eating)> SeedAsync(TestTenantContext tenant)
    {
        await using var db = fixture.CreateProductionStyleContext(tenant);
        var dining = Category.Create(TenantId.From(tenant.TenantId), new CategoryPath("Food/Dining"), "Dining");
        var eating = Category.Create(TenantId.From(tenant.TenantId), new CategoryPath("Food/Eating"), "Eating");
        db.Categories.AddRange(dining, eating);
        await db.SaveChangesAsync();
        var budgets = new EfBudgetStore(db, tenant);
        await budgets.SetAsync(tenant.TenantId, "Food/Dining", 100m);
        await budgets.SetAsync(tenant.TenantId, "Food/Eating", 50m);
        return (dining.Id, eating.Id);
    }

    [DockerFact]
    public async Task A_merge_repoints_budgets_and_categories_for_this_tenant_only()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var (dining, eating) = await SeedAsync(a);
        await SeedAsync(b);

        await using (var db = fixture.CreateProductionStyleContext(a))
        {
            var plan = new CategoryRestructurePlan([], [new CategoryMerge(eating, "Food/Eating", dining, "Food/Dining")]);
            await new EfCategoryMaintenanceStore(db, a).ApplyAsync(plan);
        }

        await using (var db = fixture.CreateProductionStyleContext(a))
        {
            (await db.Categories.Select(c => c.Id).ToListAsync()).Should().BeEquivalentTo([dining], "the merged source category is gone");
            var budgets = await new EfBudgetStore(db, a).ListAsync(a.TenantId);
            budgets.Should().ContainSingle().Which.Should().Match<FlowLedger.Application.Features.Budgets.BudgetRow>(
                r => r.Category == "Food/Dining" && r.MonthlyLimit == 150m);
        }

        await using (var db = fixture.CreateProductionStyleContext(b))
        {
            (await db.Categories.CountAsync()).Should().Be(2, "tenant B's categories are untouched");
            (await new EfBudgetStore(db, b).ListAsync(b.TenantId)).Should().HaveCount(2);
        }
    }

    [DockerFact]
    public async Task A_move_renames_the_category_and_its_budget()
    {
        var a = TestTenantContext.New();
        var (dining, _) = await SeedAsync(a);

        await using (var db = fixture.CreateProductionStyleContext(a))
        {
            var plan = new CategoryRestructurePlan([new CategoryMove(dining, "Food/Dining", "Food/Restaurants", "Restaurants", null)], []);
            await new EfCategoryMaintenanceStore(db, a).ApplyAsync(plan);
        }

        await using var check = fixture.CreateProductionStyleContext(a);
        (await check.Categories.SingleAsync(c => c.Id == dining)).Path.Value.Should().Be("Food/Restaurants");
        (await new EfBudgetStore(check, a).ListAsync(a.TenantId)).Select(r => r.Category).Should().Contain("Food/Restaurants").And.NotContain("Food/Dining");
    }
}
