using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class RecurringFlowTests
{
    private static readonly TenantId TenantId = TenantId.New();
    private static readonly AccountId AccountId = AccountId.New();
    private static readonly DateOnly Jan1 = new(2024, 1, 1);
    private static readonly DateOnly Dec31 = new(2024, 12, 31);
    private static readonly Money Rent = new(1500m, "USD");

    private static RecurringFlow MakeFlow(DateOnly? end = null) =>
        RecurringFlow.Create(
            TenantId, AccountId, "Rent",
            Rent, TransactionDirection.Debit,
            RecurrencePattern.Monthly(1),
            Jan1, end);

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_valid_flow_raises_RecurringFlowCreated()
    {
        var flow = MakeFlow(Dec31);
        flow.DomainEvents.Should().ContainSingle(e => e is RecurringFlowCreated);
    }

    [Fact]
    public void Create_empty_name_throws()
    {
        var act = () => RecurringFlow.Create(
            TenantId, AccountId, "  ", Rent, TransactionDirection.Debit,
            RecurrencePattern.Monthly(1), Jan1);
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void Create_zero_amount_throws()
    {
        var act = () => RecurringFlow.Create(
            TenantId, AccountId, "Payroll", Money.Zero("USD"),
            TransactionDirection.Credit, RecurrencePattern.Monthly(15), Jan1);
        act.Should().Throw<NegativeOrZeroAmountException>();
    }

    // ── GenerateOccurrence ────────────────────────────────────────────────────

    [Fact]
    public void GenerateOccurrence_within_window_adds_occurrence_and_raises_event()
    {
        var flow = MakeFlow(Dec31);
        flow.ClearDomainEvents();

        var date = new DateOnly(2024, 3, 1);
        var occurrence = flow.GenerateOccurrence(date);

        occurrence.PlannedDate.Should().Be(date);
        occurrence.PlannedAmount.Should().Be(Rent);
        flow.Occurrences.Should().ContainSingle(o => o.PlannedOccurrenceId == occurrence.PlannedOccurrenceId);
        flow.DomainEvents.Should().ContainSingle(e => e is PlannedOccurrenceGenerated);
    }

    [Fact]
    public void GenerateOccurrence_outside_window_throws()
    {
        var flow = MakeFlow(new DateOnly(2024, 6, 1));
        var act = () => flow.GenerateOccurrence(new DateOnly(2024, 7, 1));
        act.Should().Throw<OccurrenceDateOutOfRangeException>();
    }

    [Fact]
    public void GenerateOccurrence_with_override_amount_uses_override()
    {
        var flow = MakeFlow(Dec31);
        var overrideAmt = new Money(1600m, "USD");
        var occurrence = flow.GenerateOccurrence(new DateOnly(2024, 4, 1), overrideAmt);
        occurrence.PlannedAmount.Should().Be(overrideAmt);
    }

    [Fact]
    public void GenerateOccurrence_override_currency_mismatch_throws()
    {
        var flow = MakeFlow(Dec31);
        var act = () => flow.GenerateOccurrence(new DateOnly(2024, 4, 1), new Money(1500m, "EUR"));
        act.Should().Throw<CurrencyMismatchException>();
    }

    // ── UpdateAmount ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateAmount_changes_amount_and_model()
    {
        var flow = MakeFlow(Dec31);
        flow.UpdateAmount(new Money(1600m, "USD"), AmountModel.Estimated);
        flow.Amount.Amount.Should().Be(1600m);
        flow.AmountModel.Should().Be(AmountModel.Estimated);
    }

    [Fact]
    public void UpdateAmount_zero_throws()
    {
        var flow = MakeFlow(Dec31);
        var act = () => flow.UpdateAmount(Money.Zero("USD"), AmountModel.Fixed);
        act.Should().Throw<NegativeOrZeroAmountException>();
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    [Fact]
    public void Deactivate_sets_IsActive_false()
    {
        var flow = MakeFlow(Dec31);
        flow.Deactivate();
        flow.IsActive.Should().BeFalse();
    }

    // ── TenantId guard ────────────────────────────────────────────────────────

    [Fact]
    public void Flow_TenantId_matches_creation_tenant()
    {
        var flow = MakeFlow(Dec31);
        flow.TenantId.Should().Be(TenantId);
    }
}
