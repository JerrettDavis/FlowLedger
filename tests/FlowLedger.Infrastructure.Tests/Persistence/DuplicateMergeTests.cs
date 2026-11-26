using FlowLedger.Application.Features.Duplicates;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class DuplicateMergeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var conn = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "DROP TABLE IF EXISTS merged_transactions; DROP TABLE IF EXISTS duplicate_dismissals;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Transaction Tx(TenantId t, AccountId a, string desc, DateOnly d, string? pid)
        => Transaction.RecordActual(t, a, new Money(18.40m, Currency.Usd), TransactionDirection.Debit, desc, d, d,
            pid is null ? TransactionSource.Manual : TransactionSource.MxAggregation,
            TransactionFingerprint.FromRawDescription(a, d, 18.40m, desc, pid));

    [DockerFact]
    public async Task Merge_commits_under_the_production_retrying_execution_strategy()
    {
        var tenantCtx = TestTenantContext.New();
        var tenant = TenantId.From(tenantCtx.TenantId);
        var acct = AccountId.New();
        var day = new DateOnly(2026, 11, 10);
        var survivor = Tx(tenant, acct, "SQ *COFFEE HOUSE TULSA OK", day, "TRN-9");
        var duplicate = Tx(tenant, acct, "Coffee House Tulsa", day.AddDays(1), null);

        await using (var seed = fixture.CreateProductionStyleContext(tenantCtx))
        {
            seed.Transactions.AddRange(survivor, duplicate);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateProductionStyleContext(tenantCtx))
        {
            var store = new DuplicateStore(db, tenantCtx);
            var s = await db.Transactions.SingleAsync(t => t.Id == survivor.Id);
            var d = await db.Transactions.SingleAsync(t => t.Id == duplicate.Id);
            await store.MergeAsync(s, d, auto: false, "test", "{}");
        }

        await using var verify = fixture.CreateProductionStyleContext(tenantCtx);
        (await verify.Transactions.AnyAsync(t => t.Id == duplicate.Id)).Should().BeFalse();
        (await new DuplicateStore(verify, tenantCtx).RecentMergesAsync(5)).Should().ContainSingle();
    }

    [DockerFact]
    public async Task Auto_merge_removes_the_imported_twin_keeps_its_category_and_blocks_it_from_coming_back()
    {
        var tenantCtx = TestTenantContext.New();
        var tenant = TenantId.From(tenantCtx.TenantId);
        var acct = AccountId.New();
        var day = new DateOnly(2026, 11, 10);
        var bank = Tx(tenant, acct, "SQ *COFFEE HOUSE TULSA OK", day, "TRN-9");
        var csv = Tx(tenant, acct, "Coffee House Tulsa", day.AddDays(1), null);
        var cat = CategoryId.New();
        csv.Categorize(cat, null, CategorySource.Manual);
        csv.AppendNote("team lunch");
        var bankFingerprint = bank.Fingerprint!.Value;

        await using (var seed = fixture.CreateDbContext(tenantCtx))
        {
            seed.Transactions.AddRange(bank, csv);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext(tenantCtx))
        {
            var service = new DuplicateService(new DuplicateStore(db, tenantCtx), new DuplicateOptions());
            (await service.ScanAsync()).AutoMergeable.Should().Be(1);
            (await service.AutoMergeAsync()).Merged.Should().Be(1);
            (await service.AutoMergeAsync()).Merged.Should().Be(0, "a second pass finds nothing");
            (await service.RecentMergesAsync()).Should().ContainSingle(m => m.Auto && m.SnapshotJson.Contains("COFFEE HOUSE"));
        }

        await using (var verify = fixture.CreateDbContext(tenantCtx))
        {
            var rows = await verify.Transactions.ToListAsync();
            var kept = rows.Should().ContainSingle().Subject;
            kept.Id.Should().Be(csv.Id, "the row the user categorised and annotated survives; the bank row is folded into it");
            kept.CategoryId.Should().Be(cat);
            kept.Notes.Should().Contain("team lunch");

            var repo = new TransactionRepository(verify);
            (await repo.GetExistingFingerprintsAsync([bankFingerprint])).Should().Contain(bankFingerprint,
                "the next sync must not resurrect the bank row that was merged away");
        }
    }

    [DockerFact]
    public async Task Dismissed_pairs_are_not_suggested_again_and_possible_pairs_are_not_auto_merged()
    {
        var tenantCtx = TestTenantContext.New();
        var tenant = TenantId.From(tenantCtx.TenantId);
        var acct = AccountId.New();
        var day = new DateOnly(2026, 11, 10);
        var a = Tx(tenant, acct, "365 VEND DDA PIN POS", day, "TRN-1");
        var b = Tx(tenant, acct, "365 VEND DDA PIN POS", day, "TRN-2");
        await using (var seed = fixture.CreateDbContext(tenantCtx))
        {
            seed.Transactions.AddRange(a, b);
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext(tenantCtx);
        var service = new DuplicateService(new DuplicateStore(db, tenantCtx), new DuplicateOptions());
        var scan = await service.ScanAsync();
        scan.Suggestions.Should().ContainSingle().Which.Confidence.Should().Be("Possible");
        (await service.AutoMergeAsync()).Merged.Should().Be(0);

        await service.DismissAsync(a.Id, b.Id);
        (await service.ScanAsync()).Total.Should().Be(0);
        (await db.Transactions.CountAsync()).Should().Be(2);
    }
}
