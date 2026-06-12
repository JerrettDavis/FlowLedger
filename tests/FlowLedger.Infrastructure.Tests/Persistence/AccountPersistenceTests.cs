using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests: Account aggregate round-trip persistence and tenant isolation.
/// Requires a live Docker daemon to run; skips cleanly when Docker is unavailable.
/// </summary>
[Collection("Integration")]
public sealed class AccountPersistenceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public AccountPersistenceTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Account_RoundTrip_PersistsAndReloads()
    {
        // Arrange
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);
        var usd = Currency.Usd;
        var account = Account.Create(tenantId, "Checking Account", AccountType.Checking,
            new Money(1_500.00m, usd), institution: "First Bank");

        // Act — persist
        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Accounts.Add(account);
            await ctx.SaveChangesAsync();
        }

        // Assert — reload
        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var loaded = await ctx.Accounts.SingleAsync(a => a.Id == account.Id);
            Assert.Equal(account.Name, loaded.Name);
            Assert.Equal(1_500.00m, loaded.CurrentBalance.Amount);
            Assert.Equal("USD", loaded.CurrentBalance.Currency.Code);
            Assert.Equal(AccountType.Checking, loaded.AccountType);
            Assert.Equal("First Bank", loaded.Institution);
            Assert.True(loaded.IsActive);
        }
    }

    [DockerFact]
    public async Task Account_Money_UsesDecimalNotFloat()
    {
        // This test guards against regression to float — 0.1 + 0.2 must equal 0.3 exactly.
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);
        var amount = new Money(0.1m + 0.2m, Currency.Usd);

        var account = Account.Create(tenantId, "Precision Test", AccountType.Cash, amount);

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Accounts.Add(account);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var loaded = await ctx.Accounts.SingleAsync(a => a.Id == account.Id);
            // decimal arithmetic: 0.1m + 0.2m = 0.3m exactly
            Assert.Equal(0.3m, loaded.CurrentBalance.Amount);
        }
    }

    [DockerFact]
    public async Task Account_TenantFilter_BlocksCrossTenantRead()
    {
        // Arrange — two tenants
        var tenantA = TestTenantContext.New();
        var tenantB = TestTenantContext.New();
        var tidA = TenantId.From(tenantA.TenantId);
        var tidB = TenantId.From(tenantB.TenantId);

        var accountA = Account.Create(tidA, "Tenant A Account", AccountType.Checking, new Money(100m, Currency.Usd));
        var accountB = Account.Create(tidB, "Tenant B Account", AccountType.Savings, new Money(200m, Currency.Usd));

        // Save tenant A account using tenant A context
        await using (var ctx = _fixture.CreateDbContext(tenantA))
        {
            ctx.Accounts.Add(accountA);
            await ctx.SaveChangesAsync();
        }

        // Save tenant B account using tenant B context
        await using (var ctx = _fixture.CreateDbContext(tenantB))
        {
            ctx.Accounts.Add(accountB);
            await ctx.SaveChangesAsync();
        }

        // Assert — tenant A cannot see tenant B's account
        await using (var ctx = _fixture.CreateDbContext(tenantA))
        {
            var accounts = await ctx.Accounts.ToListAsync();
            Assert.Single(accounts);
            Assert.Equal(accountA.Id, accounts[0].Id);
            Assert.DoesNotContain(accounts, a => a.Id == accountB.Id);
        }

        // Assert — tenant B cannot see tenant A's account
        await using (var ctx = _fixture.CreateDbContext(tenantB))
        {
            var accounts = await ctx.Accounts.ToListAsync();
            Assert.Single(accounts);
            Assert.Equal(accountB.Id, accounts[0].Id);
            Assert.DoesNotContain(accounts, a => a.Id == accountA.Id);
        }
    }

    [DockerFact]
    public async Task Account_CreditLimit_PersistsNullable()
    {
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);

        // Without credit limit
        var noLimit = Account.Create(tenantId, "No Limit", AccountType.Checking, new Money(500m, Currency.Usd));
        // With credit limit
        var withLimit = Account.Create(tenantId, "CC", AccountType.CreditCard,
            new Money(-200m, Currency.Usd), creditLimit: new Money(5_000m, Currency.Usd));

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Accounts.AddRange(noLimit, withLimit);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var loadedNoLimit = await ctx.Accounts.SingleAsync(a => a.Id == noLimit.Id);
            var loadedWithLimit = await ctx.Accounts.SingleAsync(a => a.Id == withLimit.Id);

            Assert.Null(loadedNoLimit.CreditLimit);
            Assert.NotNull(loadedWithLimit.CreditLimit);
            Assert.Equal(5_000m, loadedWithLimit.CreditLimit!.Amount);
            Assert.Equal("USD", loadedWithLimit.CreditLimit.Currency.Code);
        }
    }
}
