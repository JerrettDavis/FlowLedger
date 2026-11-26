using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

/// <summary>#127: the instants an aggregate stamps (and the events it raises) come from the supplied <see cref="TimeProvider"/>.</summary>
public sealed class InjectedClockTests
{
    private static readonly DateTimeOffset Fixed = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly Money Usd = new(100m, "USD");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Clock = new FixedClock(Fixed);

    [Fact]
    public void Transaction_creation_time_uses_the_supplied_clock()
    {
        var tx = Transaction.RecordActual(Tenant, AccountId.New(), Usd, TransactionDirection.Debit, "Coffee",
            new DateOnly(2031, 3, 1), new DateOnly(2031, 3, 1), TransactionSource.Manual, time: Clock);

        tx.CreatedAt.Should().Be(Fixed);
    }

    [Fact]
    public void Planned_transaction_uses_the_supplied_clock()
        => Transaction.CreatePlanned(Tenant, AccountId.New(), Usd, TransactionDirection.Debit, "Rent", new DateOnly(2031, 4, 1), Clock)
            .CreatedAt.Should().Be(Fixed);

    [Fact]
    public void Account_creation_and_balance_changes_use_the_supplied_clock()
    {
        var account = Account.Create(Tenant, "Checking", AccountType.Checking, Usd, time: Clock);
        account.CreatedAt.Should().Be(Fixed);

        var later = new FixedClock(Fixed.AddDays(2));
        account.ApplyProviderBalance(new Money(50m, "USD"), later);

        account.LastBalanceConfirmedAt.Should().Be(Fixed.AddDays(2));
    }

    [Fact]
    public void Recurring_flow_creation_uses_the_supplied_clock()
    {
        var flow = RecurringFlow.Create(Tenant, AccountId.New(), "Rent", Usd, TransactionDirection.Debit,
            RecurrencePattern.Monthly(1), new DateOnly(2031, 1, 1), time: Clock);

        flow.CreatedAt.Should().Be(Fixed);
    }

    [Fact]
    public void Asset_creation_and_valuations_use_the_supplied_clock()
    {
        var asset = Asset.Create(Tenant, "Car", AssetCategory.Vehicle, Usd, new DateOnly(2031, 3, 1), time: Clock);
        asset.CreatedAt.Should().Be(Fixed);
        asset.Valuations.Single().RecordedAt.Should().Be(Fixed);

        var later = new FixedClock(Fixed.AddDays(9));
        var valuation = asset.RecordValuation(new Money(90m, "USD"), new DateOnly(2031, 3, 5), "manual", time: later);

        valuation.RecordedAt.Should().Be(Fixed.AddDays(9));
    }
}
