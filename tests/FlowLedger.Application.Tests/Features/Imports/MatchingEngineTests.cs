using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Imports;

public sealed class MatchingEngineTests
{
    private static readonly TenantId Tenant = TenantId.From(FakeTenantContext.DefaultTenantId);
    private static readonly Currency Usd = new("USD");
    private static readonly AccountId AccountA = AccountId.New();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Transaction MakeActual(
        Money amount,
        TransactionDirection direction,
        DateOnly date,
        AccountId? accountId = null)
    {
        return Transaction.RecordActual(
            Tenant,
            accountId ?? AccountA,
            amount,
            direction,
            "Test Transaction",
            date,
            date,
            TransactionSource.CsvImport);
    }

    private static RecurringFlow MakeFlowWithOccurrence(
        Money plannedAmount,
        TransactionDirection direction,
        DateOnly plannedDate,
        AccountId? accountId = null)
    {
        var pattern = RecurrencePattern.Monthly(plannedDate.Day);
        var flow = RecurringFlow.Create(
            Tenant,
            accountId ?? AccountA,
            "Test Flow",
            plannedAmount,
            direction,
            pattern,
            plannedDate.AddDays(-30));

        flow.GenerateOccurrence(plannedDate);
        return flow;
    }

    // ── High-confidence auto-match ────────────────────────────────────────────

    [Fact]
    public async Task Match_ExactAmountAndDate_AutoMatchesHighConfidence()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var amount = new Money(100m, Usd);

        var flow = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, date);
        occRepo.Seed(flow);

        var actual = MakeActual(amount, TransactionDirection.Debit, date);

        var suggestions = await engine.MatchAsync([actual]);

        actual.Status.Should().Be(TransactionStatus.Matched,
            "exact match should auto-match");
        suggestions.Should().BeEmpty();
        flow.Occurrences[0].Status.Should().Be(OccurrenceStatus.Matched);
    }

    [Fact]
    public async Task Match_ExactAmount_SlightlyDifferentDate_AutoMatchesIfWithinTolerance()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var plannedDate = new DateOnly(2024, 1, 15);
        var actualDate = plannedDate.AddDays(3); // within 7-day tolerance
        var amount = new Money(100m, Usd);

        var flow = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, plannedDate);
        occRepo.Seed(flow);

        var actual = MakeActual(amount, TransactionDirection.Debit, actualDate);

        await engine.MatchAsync([actual]);

        actual.Status.Should().Be(TransactionStatus.Matched);
    }

    // ── Ambiguous → NeedsReview ───────────────────────────────────────────────

    [Fact]
    public async Task Match_TwoEqualCandidates_FlagsNeedsReview()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var amount = new Money(100m, Usd);

        // Two occurrences with same amount and date — ambiguous
        var flow1 = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, date);
        var flow2 = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, date);
        occRepo.Seed(flow1);
        occRepo.Seed(flow2);

        var actual = MakeActual(amount, TransactionDirection.Debit, date);

        var suggestions = await engine.MatchAsync([actual]);

        // With two identical candidates, the engine should flag as NeedsReview
        actual.Status.Should().Be(TransactionStatus.NeedsReview,
            "multiple equally good candidates should require review");
        suggestions.Should().HaveCount(1);
    }

    // ── Outside tolerance ─────────────────────────────────────────────────────

    [Fact]
    public async Task Match_DateTooFar_NoMatch()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var plannedDate = new DateOnly(2024, 1, 15);
        var actualDate = plannedDate.AddDays(15); // beyond 7-day tolerance
        var amount = new Money(100m, Usd);

        var flow = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, plannedDate);
        occRepo.Seed(flow);

        var actual = MakeActual(amount, TransactionDirection.Debit, actualDate);

        var suggestions = await engine.MatchAsync([actual]);

        actual.Status.Should().Be(TransactionStatus.Posted, "date outside tolerance = no match");
        suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task Match_AmountTooFar_NoMatch()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var planned = new Money(100m, Usd);
        var actual_ = new Money(200m, Usd); // 100% different, well beyond 10%

        var flow = MakeFlowWithOccurrence(planned, TransactionDirection.Debit, date);
        occRepo.Seed(flow);

        var actual = MakeActual(actual_, TransactionDirection.Debit, date);

        await engine.MatchAsync([actual]);

        actual.Status.Should().Be(TransactionStatus.Posted);
    }

    // ── Direction mismatch ────────────────────────────────────────────────────

    [Fact]
    public async Task Match_WrongDirection_NoMatch()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var amount = new Money(100m, Usd);

        // Planned is Debit but actual is Credit
        var flow = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, date);
        occRepo.Seed(flow);

        var actual = MakeActual(amount, TransactionDirection.Credit, date);

        await engine.MatchAsync([actual]);

        actual.Status.Should().Be(TransactionStatus.Posted, "direction mismatch = no match");
    }

    // ── Already matched occurrence not reused ─────────────────────────────────

    [Fact]
    public async Task Match_TwoActuals_SameOccurrence_OnlyFirstAutoMatches()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var amount = new Money(100m, Usd);

        // One planned occurrence
        var flow = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, date);
        occRepo.Seed(flow);

        var actual1 = MakeActual(amount, TransactionDirection.Debit, date);
        var actual2 = MakeActual(amount, TransactionDirection.Debit, date);

        await engine.MatchAsync([actual1, actual2]);

        actual1.Status.Should().Be(TransactionStatus.Matched);
        // second actual has no candidate left
        actual2.Status.Should().Be(TransactionStatus.Posted);
    }

    // ── Confidence scoring ────────────────────────────────────────────────────

    [Fact]
    public async Task Match_ConfidenceScore_IsHighForExactMatch()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var amount = new Money(100m, Usd);

        var flow = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, date);
        occRepo.Seed(flow);

        var actual = MakeActual(amount, TransactionDirection.Debit, date);
        await engine.MatchAsync([actual]);

        flow.Occurrences[0].MatchConfidence.Should().NotBeNull();
        flow.Occurrences[0].MatchConfidence!.Value.IsHigh.Should().BeTrue();
    }

    // ── Variance recording ────────────────────────────────────────────────────

    [Fact]
    public async Task Match_RecordsAmountAndDateVariance()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var plannedDate = new DateOnly(2024, 1, 15);
        var actualDate = plannedDate.AddDays(2);
        var plannedAmount = new Money(100m, Usd);
        var actualAmount = new Money(105m, Usd); // within 10%

        var flow = MakeFlowWithOccurrence(plannedAmount, TransactionDirection.Debit, plannedDate);
        occRepo.Seed(flow);

        var actual = MakeActual(actualAmount, TransactionDirection.Debit, actualDate);
        await engine.MatchAsync([actual]);

        var occ = flow.Occurrences[0];
        occ.Status.Should().Be(OccurrenceStatus.Matched);
        occ.AmountVariance.Should().NotBeNull();
        occ.AmountVariance!.Amount.Should().Be(5m); // 105 - 100
        occ.DateVarianceDays.Should().Be(2);
    }

    // ── Forecast double-count prevention ─────────────────────────────────────

    [Fact]
    public async Task Match_MatchedOccurrence_HasMatchedStatus_ForecastWontDoubleCount()
    {
        // The forecast engine excludes Matched occurrences — this test verifies the status
        // transition so the engine can filter correctly.
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var amount = new Money(100m, Usd);

        var flow = MakeFlowWithOccurrence(amount, TransactionDirection.Debit, date);
        occRepo.Seed(flow);

        var actual = MakeActual(amount, TransactionDirection.Debit, date);
        await engine.MatchAsync([actual]);

        flow.Occurrences[0].Status.Should().Be(OccurrenceStatus.Matched,
            "matched occurrence must be Matched so forecast engine suppresses it");
        actual.Status.Should().Be(TransactionStatus.Matched,
            "actual must be Matched so forecast engine counts it as reality, not plan");
    }

    // ── No planned occurrences ────────────────────────────────────────────────

    [Fact]
    public async Task Match_NoPendingOccurrences_LeavesPosted()
    {
        var occRepo = new FakePlannedOccurrenceRepository();
        var engine = new MatchingEngine(occRepo);
        var date = new DateOnly(2024, 1, 15);
        var amount = new Money(100m, Usd);

        var actual = MakeActual(amount, TransactionDirection.Debit, date);
        var suggestions = await engine.MatchAsync([actual]);

        actual.Status.Should().Be(TransactionStatus.Posted);
        suggestions.Should().BeEmpty();
    }
}
