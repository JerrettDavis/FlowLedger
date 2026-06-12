using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests: Category entity round-trip and CategoryPath value object mapping.
/// </summary>
[Collection("Integration")]
public sealed class CategoryPersistenceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public CategoryPersistenceTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Category_RoundTrip_PersistsAndReloads()
    {
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);

        var category = Category.Create(
            tenantId,
            new CategoryPath("Food/Groceries"),
            "Groceries");

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Categories.Add(category);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var loaded = await ctx.Categories.SingleAsync(c => c.Id == category.Id);
            Assert.Equal("Groceries", loaded.DisplayName);
            Assert.Equal("Food/Groceries", loaded.Path.Value);
            Assert.Equal("Food", loaded.Path.TopLevel);
            Assert.False(loaded.IsSystem);
        }
    }

    [DockerFact]
    public async Task Category_HierarchyPath_PreservesSegments()
    {
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);

        var deepCategory = Category.Create(
            tenantId,
            new CategoryPath("Finance/Banking/Fees"),
            "Bank Fees");

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Categories.Add(deepCategory);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var loaded = await ctx.Categories.SingleAsync(c => c.Id == deepCategory.Id);
            Assert.Equal(3, loaded.Path.Segments.Count);
            Assert.Equal("Finance", loaded.Path.Segments[0]);
            Assert.Equal("Banking", loaded.Path.Segments[1]);
            Assert.Equal("Fees", loaded.Path.Segments[2]);
        }
    }

    [DockerFact]
    public async Task Category_TenantFilter_BlocksCrossTenantRead()
    {
        var tenantA = TestTenantContext.New();
        var tenantB = TestTenantContext.New();
        var tidA = TenantId.From(tenantA.TenantId);
        var tidB = TenantId.From(tenantB.TenantId);

        var catA = Category.Create(tidA, new CategoryPath("Income"), "Salary");
        var catB = Category.Create(tidB, new CategoryPath("Expense"), "Rent");

        await using (var ctx = _fixture.CreateDbContext(tenantA))
        {
            ctx.Categories.Add(catA);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantB))
        {
            ctx.Categories.Add(catB);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantA))
        {
            var results = await ctx.Categories.ToListAsync();
            Assert.Single(results);
            Assert.Equal(catA.Id, results[0].Id);
        }
    }
}
