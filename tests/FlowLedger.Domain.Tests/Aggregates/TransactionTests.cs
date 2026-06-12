using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

public sealed class TransactionTests
{
    private static readonly TenantId TenantId = TenantId.New();
    private static readonly AccountId AccountId = AccountId.New();
    private static readonly Money Fifty = new(50m, "USD");
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // ── RecordActual ──────────────────────────────────────────────────────────

    [Fact]
    public void RecordActual_posted_transaction_raises_TransactionImported()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "Amazon", Today, Today, TransactionSource.CsvImport);

        tx.Status.Should().Be(TransactionStatus.Posted);
        tx.DomainEvents.Should().ContainSingle(e => e is TransactionImported);
    }

    [Fact]
    public void RecordActual_pending_transaction_has_Pending_status()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "Uber", Today, null, TransactionSource.MxAggregation);

        tx.Status.Should().Be(TransactionStatus.Pending);
    }

    [Fact]
    public void RecordActual_empty_description_throws()
    {
        var act = () => Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "  ", Today, Today, TransactionSource.Manual);
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void RecordActual_zero_amount_throws()
    {
        var act = () => Transaction.RecordActual(
            TenantId, AccountId, Money.Zero("USD"), TransactionDirection.Debit,
            "Desc", Today, Today, TransactionSource.Manual);
        act.Should().Throw<NegativeOrZeroAmountException>();
    }

    // ── CreatePlanned ─────────────────────────────────────────────────────────

    [Fact]
    public void CreatePlanned_sets_Planned_status_and_no_event_yet()
    {
        var tx = Transaction.CreatePlanned(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "Rent", Today);

        tx.Status.Should().Be(TransactionStatus.Planned);
        // Planned creation does not raise TransactionImported
        tx.DomainEvents.Should().NotContain(e => e is TransactionImported);
    }

    // ── Match ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Match_transitions_Posted_to_Matched_and_raises_event()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "Electric bill", Today, Today, TransactionSource.CsvImport);
        tx.ClearDomainEvents();

        var occurrenceId = PlannedOccurrenceId.New();
        tx.Match(occurrenceId, ConfidenceScore.High);

        tx.Status.Should().Be(TransactionStatus.Matched);
        tx.MatchedOccurrenceId.Should().Be(occurrenceId);
        tx.DomainEvents.Should().ContainSingle(e => e is TransactionMatchedToPlan);
    }

    [Fact]
    public void Match_on_Planned_transaction_throws_invalid_transition()
    {
        var tx = Transaction.CreatePlanned(
            TenantId, AccountId, Fifty, TransactionDirection.Debit, "Rent", Today);

        var act = () => tx.Match(PlannedOccurrenceId.New(), ConfidenceScore.High);
        act.Should().Throw<InvalidStatusTransitionException>();
    }

    // ── Reconcile ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reconcile_from_Matched_succeeds()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "Rent", Today, Today, TransactionSource.CsvImport);
        tx.Match(PlannedOccurrenceId.New(), ConfidenceScore.High);

        tx.Reconcile();
        tx.Status.Should().Be(TransactionStatus.Reconciled);
    }

    [Fact]
    public void Reconcile_from_Planned_throws()
    {
        var tx = Transaction.CreatePlanned(TenantId, AccountId, Fifty, TransactionDirection.Debit, "Rent", Today);
        var act = () => tx.Reconcile();
        act.Should().Throw<InvalidStatusTransitionException>();
    }

    // ── Skip ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Skip_on_Planned_sets_Skipped()
    {
        var tx = Transaction.CreatePlanned(TenantId, AccountId, Fifty, TransactionDirection.Debit, "Bonus", Today);
        tx.Skip();
        tx.Status.Should().Be(TransactionStatus.Skipped);
    }

    [Fact]
    public void Skip_on_Posted_throws()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit, "Paid", Today, Today, TransactionSource.Manual);
        var act = () => tx.Skip();
        act.Should().Throw<InvalidStatusTransitionException>();
    }

    // ── Categorize ────────────────────────────────────────────────────────────

    [Fact]
    public void Categorize_sets_category_and_raises_event()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit, "Grocery", Today, Today, TransactionSource.Manual);
        tx.ClearDomainEvents();

        var catId = CategoryId.New();
        tx.Categorize(catId, "Whole Foods");

        tx.CategoryId.Should().Be(catId);
        tx.MerchantName.Should().Be("Whole Foods");
        tx.DomainEvents.Should().ContainSingle(e => e is TransactionCategorized);
    }

    // ── Splits ────────────────────────────────────────────────────────────────

    [Fact]
    public void SetSplits_valid_splits_sum_correctly()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, new Money(100m, "USD"), TransactionDirection.Debit,
            "Department store", Today, Today, TransactionSource.Manual);

        var splits = new[]
        {
            new TransactionSplit(new Money(60m, "USD")),
            new TransactionSplit(new Money(40m, "USD")),
        };

        tx.SetSplits(splits);
        tx.Splits.Should().HaveCount(2);
    }

    [Fact]
    public void SetSplits_mismatched_total_throws()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, new Money(100m, "USD"), TransactionDirection.Debit,
            "Store", Today, Today, TransactionSource.Manual);

        var splits = new[]
        {
            new TransactionSplit(new Money(60m, "USD")),
            new TransactionSplit(new Money(50m, "USD")), // 110 != 100
        };

        var act = () => tx.SetSplits(splits);
        act.Should().Throw<SplitAmountMismatchException>();
    }

    // ── MarkPosted ────────────────────────────────────────────────────────────

    [Fact]
    public void MarkPosted_transitions_Pending_to_Posted()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "Pending charge", Today, null, TransactionSource.MxAggregation);

        tx.MarkPosted(Today);

        tx.Status.Should().Be(TransactionStatus.Posted);
        tx.PostedDate.Should().Be(Today);
    }

    [Fact]
    public void MarkPosted_on_non_Pending_throws()
    {
        var tx = Transaction.RecordActual(
            TenantId, AccountId, Fifty, TransactionDirection.Debit,
            "Already posted", Today, Today, TransactionSource.Manual);

        var act = () => tx.MarkPosted(Today);
        act.Should().Throw<InvalidStatusTransitionException>();
    }
}
