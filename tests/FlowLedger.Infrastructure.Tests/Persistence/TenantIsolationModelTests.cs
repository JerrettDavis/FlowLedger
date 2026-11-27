using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// Seeded model test for INV-01 (#178): rows written by several tenants into the same tables are visible to their owner and to nobody
/// else. The model is "each tenant owns exactly the ids it created"; the system under test is the tenant query filter on the real
/// database. Deterministic: a fixed list of seeds drives <see cref="Random"/>.
/// </summary>
[Collection("Integration")]
[Trait("Invariant", "INV-01")]
public sealed class TenantIsolationModelTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public TenantIsolationModelTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 8).Select(s => new object[] { s });

    [DockerTheory]
    [MemberData(nameof(Seeds))]
    public async Task Every_tenant_reads_exactly_the_rows_it_wrote_and_cannot_fetch_anothers_by_id(int seed)
    {
        var rng = new Random(seed);
        var tenants = Enumerable.Range(0, 3).Select(_ => TestTenantContext.New()).ToList();
        var owned = tenants.ToDictionary(t => t.TenantId, _ => (Accounts: new List<Guid>(), Transactions: new List<Guid>()));

        foreach (var tenant in tenants)
        {
            var tenantId = TenantId.From(tenant.TenantId);
            await using var db = _fixture.CreateDbContext(tenant);
            var accountCount = rng.Next(1, 4);
            for (var a = 0; a < accountCount; a++)
            {
                var account = Account.Create(tenantId, $"Acct {a}", AccountType.Checking, new Money(100m, Currency.Usd), TestClock.Provider);
                db.Accounts.Add(account);
                owned[tenant.TenantId].Accounts.Add(account.AccountId.Value);
                for (var t = rng.Next(0, 6); t > 0; t--)
                {
                    var tx = Transaction.RecordActual(
                        tenantId, account.AccountId, new Money(1m + rng.Next(1, 500), Currency.Usd), TransactionDirection.Debit,
                        $"tx {tenant.TenantId:N} {rng.Next()}", TestClock.Today, TestClock.Today, TransactionSource.Manual, TestClock.Provider);
                    db.Transactions.Add(tx);
                    owned[tenant.TenantId].Transactions.Add(tx.Id);
                }
            }

            await db.SaveChangesAsync();
        }

        foreach (var tenant in tenants)
        {
            await using var db = _fixture.CreateDbContext(tenant);
            var mine = owned[tenant.TenantId];
            var others = tenants.Where(t => t.TenantId != tenant.TenantId).Select(t => owned[t.TenantId]).ToList();

            (await db.Accounts.AsNoTracking().Select(a => a.Id).ToListAsync()).Should().BeEquivalentTo(mine.Accounts, "seed {0}", seed);
            (await db.Transactions.AsNoTracking().Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(mine.Transactions, "seed {0}", seed);

            // Asking for another tenant's row by its exact id finds nothing.
            foreach (var foreign in others.SelectMany(o => o.Transactions).Take(10))
            {
                (await db.Transactions.AsNoTracking().AnyAsync(t => t.Id == foreign)).Should().BeFalse("seed {0}: a foreign id is invisible", seed);
            }

            foreach (var foreign in others.SelectMany(o => o.Accounts))
            {
                (await db.Accounts.AsNoTracking().AnyAsync(a => a.Id == foreign)).Should().BeFalse("seed {0}: a foreign account id is invisible", seed);
            }
        }
    }
}
