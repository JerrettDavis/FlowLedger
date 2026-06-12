using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class PlannedFlowOccurrenceTests
{
    private static readonly TenantId TenantId = TenantId.New();
    private static readonly AccountId AccountId = AccountId.New();
    private static readonly DateOnly Jan1 = new(2024, 1, 1);
    private static readonly DateOnly Dec31 = new(2024, 12, 31);

    private static RecurringFlow MakeFlow() =>
        RecurringFlow.Create(
            TenantId, AccountId, "Electric",
            new Money(180m, "USD"), TransactionDirection.Debit,
            RecurrencePattern.Monthly(10), Jan1, Dec31);

    // ── MatchActual ───────────────────────────────────────────────────────────

    [Fact]
    public void MatchActual_records_variance_and_transitions_to_Matched()
    {
        var flow = MakeFlow();
        var occ = flow.GenerateOccurrence(new DateOnly(2024, 6, 10));

        var actualTxId = TransactionId.New();
        var actualAmount = new Money(184.25m, "USD");
        var actualDate = new DateOnly(2024, 6, 11);

        occ.MatchActual(actualTxId, actualAmount, actualDate, ConfidenceScore.High);

        occ.Status.Should().Be(OccurrenceStatus.Matched);
        occ.MatchedTransactionId.Should().Be(actualTxId);
        occ.AmountVariance!.Amount.Should().Be(4.25m); // 184.25 - 180.00
        occ.DateVarianceDays.Should().Be(1);            // 11th - 10th
        occ.MatchConfidence!.Value.Value.Should().Be(ConfidenceScore.High.Value);
    }

    [Fact]
    public void MatchActual_second_match_throws_OccurrenceAlreadyMatchedException()
    {
        var flow = MakeFlow();
        var occ = flow.GenerateOccurrence(new DateOnly(2024, 6, 10));
        occ.MatchActual(TransactionId.New(), new Money(180m, "USD"), new DateOnly(2024, 6, 10), ConfidenceScore.High);

        var act = () => occ.MatchActual(TransactionId.New(), new Money(180m, "USD"), new DateOnly(2024, 6, 10), ConfidenceScore.High);
        act.Should().Throw<OccurrenceAlreadyMatchedException>();
    }

    [Fact]
    public void MatchActual_currency_mismatch_throws()
    {
        var flow = MakeFlow();
        var occ = flow.GenerateOccurrence(new DateOnly(2024, 6, 10));

        var act = () => occ.MatchActual(TransactionId.New(), new Money(180m, "EUR"), new DateOnly(2024, 6, 10), ConfidenceScore.High);
        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void MatchActual_zero_variance_when_amounts_equal()
    {
        var flow = MakeFlow();
        var occ = flow.GenerateOccurrence(new DateOnly(2024, 6, 10));
        occ.MatchActual(TransactionId.New(), new Money(180m, "USD"), new DateOnly(2024, 6, 10), ConfidenceScore.Certain);

        occ.AmountVariance!.IsZero.Should().BeTrue();
        occ.DateVarianceDays.Should().Be(0);
    }

    // ── Skip ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Skip_transitions_Pending_to_Skipped()
    {
        var flow = MakeFlow();
        var occ = flow.GenerateOccurrence(new DateOnly(2024, 6, 10));
        occ.Skip();
        occ.Status.Should().Be(OccurrenceStatus.Skipped);
    }

    [Fact]
    public void Skip_already_matched_throws()
    {
        var flow = MakeFlow();
        var occ = flow.GenerateOccurrence(new DateOnly(2024, 6, 10));
        occ.MatchActual(TransactionId.New(), new Money(180m, "USD"), new DateOnly(2024, 6, 10), ConfidenceScore.High);

        var act = () => occ.Skip();
        act.Should().Throw<InvalidStatusTransitionException>();
    }

    [Fact]
    public void MatchActual_on_Skipped_occurrence_throws()
    {
        var flow = MakeFlow();
        var occ = flow.GenerateOccurrence(new DateOnly(2024, 6, 10));
        occ.Skip();

        var act = () => occ.MatchActual(TransactionId.New(), new Money(180m, "USD"), new DateOnly(2024, 6, 10), ConfidenceScore.High);
        act.Should().Throw<InvalidStatusTransitionException>();
    }
}
