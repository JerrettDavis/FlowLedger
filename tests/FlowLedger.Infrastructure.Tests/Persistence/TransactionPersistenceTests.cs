using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests: Transaction aggregate round-trip, fingerprint uniqueness, and tenant isolation.
/// </summary>
[Collection("Integration")]
public sealed class TransactionPersistenceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;

    public TransactionPersistenceTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Transaction_RoundTrip_PersistsAndReloads()
    {
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);
        var accountId = AccountId.New();
        var date = new DateOnly(2025, 6, 1);

        var tx = Transaction.RecordActual(
            tenantId, accountId,
            new Money(125.50m, Currency.Usd),
            TransactionDirection.Debit,
            "Grocery Store",
            effectiveDate: date,
            postedDate: date,
            source: TransactionSource.Manual);

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Transactions.Add(tx);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var loaded = await ctx.Transactions.SingleAsync(t => t.Id == tx.Id);
            Assert.Equal(125.50m, loaded.Amount.Amount);
            Assert.Equal("USD", loaded.Amount.Currency.Code);
            Assert.Equal("Grocery Store", loaded.Description);
            Assert.Equal(TransactionStatus.Posted, loaded.Status);
            Assert.Equal(date, loaded.EffectiveDate);
            Assert.Equal(date, loaded.PostedDate);
        }
    }

    [DockerFact]
    public async Task Transaction_Fingerprint_UniquenessConstraintEnforced()
    {
        // The fingerprint unique index (per tenant) must reject a duplicate insert.
        // We use separate contexts so EF change tracking doesn't interfere — the
        // constraint fires at the PostgreSQL level during the second insert.
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);
        var accountId = AccountId.New();
        var date = new DateOnly(2025, 6, 10);

        var fingerprint = TransactionFingerprint.Create(accountId, date, 99.99m, "MERCHANT ABC");

        var tx1 = Transaction.RecordActual(tenantId, accountId,
            new Money(99.99m, Currency.Usd), TransactionDirection.Debit,
            "MERCHANT ABC", date, date, TransactionSource.CsvImport, fingerprint);

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Transactions.Add(tx1);
            await ctx.SaveChangesAsync();
        }

        // Attempting to insert a second transaction with the same fingerprint in the
        // same tenant should fail with a DB unique constraint violation.
        var tx2 = Transaction.RecordActual(tenantId, accountId,
            new Money(99.99m, Currency.Usd), TransactionDirection.Debit,
            "MERCHANT ABC (dup)", date, date, TransactionSource.CsvImport, fingerprint);

        await using (var ctx2 = _fixture.CreateDbContext(tenantCtx))
        {
            ctx2.Transactions.Add(tx2);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
            Assert.NotNull(ex);
        }
    }

    [DockerFact]
    public async Task Transaction_Fingerprint_CrossTenant_AllowsDuplicate()
    {
        // Same fingerprint is allowed across different tenants.
        var tenantA = TestTenantContext.New();
        var tenantB = TestTenantContext.New();
        var tidA = TenantId.From(tenantA.TenantId);
        var tidB = TenantId.From(tenantB.TenantId);
        var accountId = AccountId.New();
        var date = new DateOnly(2025, 7, 1);

        var fingerprint = TransactionFingerprint.Create(accountId, date, 50m, "CROSS TENANT STORE");

        var txA = Transaction.RecordActual(tidA, accountId, new Money(50m, Currency.Usd),
            TransactionDirection.Debit, "CROSS TENANT STORE", date, date, TransactionSource.Manual, fingerprint);

        var txB = Transaction.RecordActual(tidB, accountId, new Money(50m, Currency.Usd),
            TransactionDirection.Debit, "CROSS TENANT STORE", date, date, TransactionSource.Manual, fingerprint);

        await using (var ctx = _fixture.CreateDbContext(tenantA))
        {
            ctx.Transactions.Add(txA);
            await ctx.SaveChangesAsync();
        }

        // Should succeed — different tenant so no fingerprint collision.
        await using (var ctx = _fixture.CreateDbContext(tenantB))
        {
            ctx.Transactions.Add(txB);
            await ctx.SaveChangesAsync(); // no exception expected
        }
    }

    [DockerFact]
    public async Task Transaction_TenantFilter_BlocksCrossTenantRead()
    {
        var tenantA = TestTenantContext.New();
        var tenantB = TestTenantContext.New();
        var tidA = TenantId.From(tenantA.TenantId);
        var tidB = TenantId.From(tenantB.TenantId);
        var accountId = AccountId.New();

        var txA = Transaction.RecordActual(tidA, accountId, new Money(10m, Currency.Usd),
            TransactionDirection.Debit, "TxA", new DateOnly(2025, 1, 1), null, TransactionSource.Manual);

        var txB = Transaction.RecordActual(tidB, accountId, new Money(20m, Currency.Usd),
            TransactionDirection.Debit, "TxB", new DateOnly(2025, 1, 2), null, TransactionSource.Manual);

        await using (var ctx = _fixture.CreateDbContext(tenantA))
        {
            ctx.Transactions.Add(txA);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantB))
        {
            ctx.Transactions.Add(txB);
            await ctx.SaveChangesAsync();
        }

        // Tenant A should only see their transaction
        await using (var ctx = _fixture.CreateDbContext(tenantA))
        {
            var results = await ctx.Transactions.ToListAsync();
            Assert.Single(results);
            Assert.Equal(txA.Id, results[0].Id);
        }

        // Tenant B should only see their transaction
        await using (var ctx = _fixture.CreateDbContext(tenantB))
        {
            var results = await ctx.Transactions.ToListAsync();
            Assert.Single(results);
            Assert.Equal(txB.Id, results[0].Id);
        }
    }

    [DockerFact]
    public async Task Transaction_Splits_PersistedWithOwner()
    {
        var tenantCtx = TestTenantContext.New();
        var tenantId = TenantId.From(tenantCtx.TenantId);
        var accountId = AccountId.New();
        var catId = CategoryId.New();
        var date = new DateOnly(2025, 8, 1);

        var tx = Transaction.RecordActual(tenantId, accountId,
            new Money(100m, Currency.Usd), TransactionDirection.Debit,
            "Split Transaction", date, date, TransactionSource.Manual);

        tx.SetSplits([
            new TransactionSplit(new Money(60m, Currency.Usd), catId, "groceries"),
            new TransactionSplit(new Money(40m, Currency.Usd), notes: "household"),
        ]);

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            ctx.Transactions.Add(tx);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _fixture.CreateDbContext(tenantCtx))
        {
            var loaded = await ctx.Transactions
                .Include(t => t.Splits)
                .SingleAsync(t => t.Id == tx.Id);
            Assert.Equal(2, loaded.Splits.Count);
            Assert.Equal(60m, loaded.Splits[0].Amount.Amount);
            Assert.Equal(40m, loaded.Splits[1].Amount.Amount);
        }
    }
}
