using CsCheck;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Tests.Helpers;
using FlowLedger.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>
/// Money survives the database exactly (#177): every money column is <c>numeric(19,4)</c>, so any amount with up to four decimals and up to
/// fifteen integer digits is stored and read back digit for digit, and the sum of what was stored equals the sum of what was written
/// (no per-row rounding that would make a total drift).
/// </summary>
[Collection("Integration")]
[Trait("Invariant", "INV-04")]
public sealed class MoneyPersistencePropertyTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    public MoneyPersistencePropertyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Four-decimal amounts from 0.0001 up to the column's ceiling of 900 trillion (well inside numeric(19,4)).</summary>
    private static readonly Gen<decimal> Amount4dp = Gen.Long[1L, 9_000_000_000_000_000_000L].Select(n => n / 10_000m);

    [DockerFact]
    public void Transaction_amounts_and_their_sum_survive_persist_and_reload_exactly()
    {
        Prop.ForAll(Amount4dp.List[1, 12], amounts =>
        {
            var tenant = TestTenantContext.New();
            var tenantId = TenantId.From(tenant.TenantId);
            using var db = _fixture.CreateDbContext(tenant);
            var account = Account.Create(tenantId, "Round trip", AccountType.Checking, new Money(0m, Currency.Usd), TestClock.Provider);
            db.Accounts.Add(account);
            foreach (var (amount, i) in amounts.Select((a, i) => (a, i)))
            {
                db.Transactions.Add(Transaction.RecordActual(
                    tenantId, account.AccountId, new Money(amount, Currency.Usd), TransactionDirection.Credit, $"row {i}",
                    TestClock.Today, TestClock.Today, TransactionSource.Manual, TestClock.Provider));
            }

            db.SaveChangesAsync().GetAwaiter().GetResult();

            using var reload = _fixture.CreateDbContext(tenant);
            var stored = reload.Transactions.AsNoTracking().OrderBy(t => t.Description).ToList().Select(t => t.Amount.Amount).ToList();
            stored.Should().BeEquivalentTo(amounts, "each amount is stored without rounding");
            reload.Transactions.Sum(t => t.Amount.Amount).Should().Be(amounts.Sum(), "a total computed by the database equals the total of the inputs");
            stored.Sum().Should().Be(amounts.Sum());
        }, iterations: 30);
    }

    [DockerFact]
    public void Account_balances_and_credit_limits_keep_four_decimals()
    {
        Prop.ForAll(Gen.Select(Amount4dp, Amount4dp), pair =>
        {
            var tenant = TestTenantContext.New();
            var tenantId = TenantId.From(tenant.TenantId);
            using var db = _fixture.CreateDbContext(tenant);
            var account = Account.Create(tenantId, "Precise", AccountType.CreditCard, new Money(pair.Item1, Currency.Usd), TestClock.Provider, new Money(pair.Item2, Currency.Usd));
            db.Accounts.Add(account);
            db.SaveChangesAsync().GetAwaiter().GetResult();

            using var reload = _fixture.CreateDbContext(tenant);
            var back = reload.Accounts.AsNoTracking().Single();
            back.CurrentBalance.Amount.Should().Be(pair.Item1);
            back.CreditLimit!.Amount.Should().Be(pair.Item2);
        }, iterations: 20);
    }
}
