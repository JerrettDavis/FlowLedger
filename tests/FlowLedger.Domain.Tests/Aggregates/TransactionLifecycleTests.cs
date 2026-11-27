using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.Events;
using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.TestSupport;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

/// <summary>#134: the status transitions and note/name helpers of the Transaction aggregate that the example tests did not reach.</summary>
public sealed class TransactionLifecycleTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly AccountId Account = AccountId.New();
    private static readonly Money Fifty = new(50m, "USD");
    private static readonly DateOnly Day = TestClock.Today;

    private static Transaction Pending() => Transaction.RecordActual(
        Tenant, Account, Fifty, TransactionDirection.Debit, "Fuel", Day, null, TransactionSource.MxAggregation, TestClock.Provider);

    private static Transaction Posted() => Transaction.RecordActual(
        Tenant, Account, Fifty, TransactionDirection.Debit, "Fuel", Day, Day, TransactionSource.Manual, TestClock.Provider);

    // ── Settle / AbandonPending / MarkPosted ──────────────────────────────────

    [Fact]
    public void Settle_posts_a_pending_transaction_adopts_the_final_amount_and_the_fingerprint()
    {
        var tx = Pending();
        var fingerprint = TransactionFingerprint.Create(Account, Day, 50m, "fuel");

        tx.Settle(Day.AddDays(1), fingerprint, new Money(58m, "USD")).Should().BeTrue();

        tx.Status.Should().Be(TransactionStatus.Posted);
        tx.PostedDate.Should().Be(Day.AddDays(1));
        tx.Amount.Amount.Should().Be(58m);
        tx.Fingerprint.Should().Be(fingerprint);
    }

    [Fact]
    public void Settle_ignores_a_final_amount_in_another_currency_or_of_zero()
    {
        var other = Pending();
        other.Settle(Day, finalAmount: new Money(60m, "EUR")).Should().BeTrue();
        other.Amount.Should().Be(Fifty);

        var zero = Pending();
        zero.Settle(Day, finalAmount: Money.Zero("USD")).Should().BeTrue();
        zero.Amount.Should().Be(Fifty);
    }

    [Fact]
    public void Settle_and_AbandonPending_do_nothing_unless_the_transaction_is_pending()
    {
        var tx = Posted();

        tx.Settle(Day.AddDays(3)).Should().BeFalse();
        tx.AbandonPending().Should().BeFalse();
        tx.Status.Should().Be(TransactionStatus.Posted);
        tx.PostedDate.Should().Be(Day);
    }

    [Fact]
    public void AbandonPending_retires_an_authorization_that_never_posted()
    {
        var tx = Pending();

        tx.AbandonPending().Should().BeTrue();

        tx.Status.Should().Be(TransactionStatus.Skipped);
    }

    [Fact]
    public void MarkPosted_posts_a_pending_transaction_and_rejects_any_other_status()
    {
        var tx = Pending();
        tx.MarkPosted(Day);
        tx.Status.Should().Be(TransactionStatus.Posted);

        var again = () => tx.MarkPosted(Day);
        again.Should().Throw<InvalidStatusTransitionException>();
    }

    // ── Review flag and unmatch ───────────────────────────────────────────────

    [Fact]
    public void FlagNeedsReview_applies_to_posted_and_pending_only()
    {
        var posted = Posted();
        posted.FlagNeedsReview();
        posted.Status.Should().Be(TransactionStatus.NeedsReview);

        var skipped = Pending();
        skipped.AbandonPending();
        skipped.FlagNeedsReview();
        skipped.Status.Should().Be(TransactionStatus.Skipped, "a retired authorization is not reviewable");
    }

    [Fact]
    public void Unmatch_returns_the_released_occurrence_and_reposts_a_matched_transaction()
    {
        var tx = Posted();
        var occurrence = PlannedOccurrenceId.New();
        tx.Match(occurrence, ConfidenceScore.High);

        tx.Unmatch().Should().Be(occurrence);

        tx.Status.Should().Be(TransactionStatus.Posted);
        tx.MatchedOccurrenceId.Should().BeNull();
    }

    [Fact]
    public void Unmatch_of_an_unmatched_posted_transaction_is_an_invalid_transition()
    {
        var act = () => Posted().Unmatch();

        act.Should().Throw<InvalidStatusTransitionException>();
    }

    // ── Names and notes ───────────────────────────────────────────────────────

    [Fact]
    public void SetSuggestedName_trims_clears_blanks_and_reports_whether_it_changed()
    {
        var tx = Posted();

        tx.SetSuggestedName("  Shell  ").Should().BeTrue();
        tx.SuggestedName.Should().Be("Shell");
        tx.SetSuggestedName("Shell").Should().BeFalse();
        tx.SetSuggestedName("   ").Should().BeTrue();
        tx.SuggestedName.Should().BeNull();
        tx.SetSuggestedName(null).Should().BeFalse();
    }

    [Fact]
    public void Notes_can_be_set_appended_and_added_with_blank_input_rejected_where_required()
    {
        var tx = Posted();

        tx.SetNote("  first ");
        tx.Notes.Should().Be("first");
        tx.AppendNote("second").Should().BeTrue();
        tx.Notes.Should().Be("first\nsecond");
        tx.SetNote("   ");
        tx.Notes.Should().BeNull();

        tx.AddNote("  replaced ");
        tx.Notes.Should().Be("replaced");
        var blank = () => tx.AddNote(" ");
        blank.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void AppendNote_keeps_only_the_newest_text_when_the_column_limit_is_exceeded()
    {
        var tx = Posted();
        tx.SetNote(new string('a', Transaction.MaxNotesLength));

        tx.AppendNote("tail").Should().BeTrue();

        tx.Notes!.Length.Should().Be(Transaction.MaxNotesLength);
        tx.Notes.Should().EndWith("tail");
    }

    // ── Domain events and ids ─────────────────────────────────────────────────

    [Fact]
    public void Post_import_events_carry_their_payload_and_the_producers_clock()
    {
        var batch = Guid.NewGuid();
        var imported = new TransactionsImported(Tenant, Account, batch, 7) { OccurredAt = TestClock.Instant };
        var synced = new SyncCompleted(Tenant, 2, 30, 4) { OccurredAt = TestClock.Instant };

        imported.ImportedCount.Should().Be(7);
        imported.ImportBatchId.Should().Be(batch);
        imported.OccurredAt.Should().Be(TestClock.Instant);
        synced.TransactionsAdded.Should().Be(30);
    }

    [Fact]
    public void Every_strongly_typed_id_round_trips_its_guid_and_prints_it()
    {
        var g = Guid.NewGuid();

        TenantId.From(g).Value.Should().Be(g);
        AccountId.From(g).ToString().Should().Be(g.ToString());
        TransactionId.From(g).ToString().Should().Be(g.ToString());
        RecurringFlowId.From(g).ToString().Should().Be(g.ToString());
        PlannedOccurrenceId.From(g).ToString().Should().Be(g.ToString());
        CategoryId.From(g).ToString().Should().Be(g.ToString());
        AssetId.From(g).ToString().Should().Be(g.ToString());
        TenantId.New().Should().NotBe(TenantId.New());
        TransactionId.New().Should().NotBe(TransactionId.New());
        RecurringFlowId.New().Should().NotBe(RecurringFlowId.New());
        PlannedOccurrenceId.New().Should().NotBe(PlannedOccurrenceId.New());
        CategoryId.New().Should().NotBe(CategoryId.New());
        AssetId.New().Should().NotBe(AssetId.New());
    }

    // ── RecurringFlow / PlannedFlowOccurrence leftovers ───────────────────────

    private static RecurringFlow Flow() => RecurringFlow.Create(
        Tenant, Account, "Rent", new Money(1200m, "USD"), TransactionDirection.Debit,
        RecurrencePattern.Monthly(1), new DateOnly(2026, 1, 1), TestClock.Provider);

    [Fact]
    public void RecurringFlow_updates_amount_pattern_and_window()
    {
        var flow = Flow();

        flow.UpdateAmount(new Money(1300m, "USD"), AmountModel.Estimated);
        flow.UpdatePattern(RecurrencePattern.Monthly(5));
        flow.ExtendWindow(new DateOnly(2026, 12, 31));

        flow.Amount.Amount.Should().Be(1300m);
        flow.AmountModel.Should().Be(AmountModel.Estimated);
        flow.Pattern.Should().Be(RecurrencePattern.Monthly(5));
        flow.ActiveWindow.End.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void RecurringFlow_rejects_a_new_amount_in_another_currency_and_a_missing_pattern()
    {
        var flow = Flow();

        var currency = () => flow.UpdateAmount(new Money(10m, "EUR"), AmountModel.Fixed);
        var pattern = () => flow.UpdatePattern(null!);

        currency.Should().Throw<CurrencyMismatchException>();
        pattern.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void An_unmatched_occurrence_cannot_be_unmatched()
    {
        var occurrence = Flow().GenerateOccurrence(new DateOnly(2026, 2, 1));

        var act = () => occurrence.Unmatch();

        act.Should().Throw<InvalidStatusTransitionException>();
    }

    [Fact]
    public void A_matched_occurrence_goes_back_to_pending_with_its_match_data_cleared()
    {
        var occurrence = Flow().GenerateOccurrence(new DateOnly(2026, 2, 1));
        occurrence.MatchActual(TransactionId.New(), new Money(1210m, "USD"), new DateOnly(2026, 2, 2), ConfidenceScore.High);

        occurrence.Unmatch();

        occurrence.Status.Should().Be(OccurrenceStatus.Pending);
        occurrence.MatchedTransactionId.Should().BeNull();
        occurrence.AmountVariance.Should().BeNull();
        occurrence.DateVarianceDays.Should().BeNull();
        occurrence.MatchConfidence.Should().BeNull();
    }
}
