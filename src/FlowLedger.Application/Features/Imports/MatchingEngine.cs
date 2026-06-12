using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Imports;

/// <summary>
/// Planned-vs-actual matching engine (PLAN.md §10.5, §11).
///
/// Algorithm:
/// 1. For each newly imported actual transaction, search pending planned occurrences
///    within a configurable date tolerance window whose account and direction match.
/// 2. Compute a confidence score from amount and date proximity.
/// 3. High-confidence (>= 0.75): auto-match — call PlannedFlowOccurrence.MatchActual
///    + Transaction.Match. Neither side is double-counted by the forecast engine
///    (the engine already excludes Matched occurrences).
/// 4. Ambiguous (>= threshold but < auto-match, or multiple candidates): mark
///    transaction NeedsReview and surface as a suggestion. Never silently mis-match.
/// 5. No match: leave transaction as Posted.
/// </summary>
public sealed class MatchingEngine
{
    // ── Tolerance defaults ─────────────────────────────────────────────────────

    /// <summary>Number of calendar days either side of the planned date to consider.</summary>
    public static readonly int DefaultDateToleranceDays = 7;

    /// <summary>Maximum allowed amount difference as a fraction of the planned amount.</summary>
    public static readonly decimal DefaultAmountToleranceFraction = 0.10m; // ±10%

    /// <summary>Confidence >= this → auto-match.</summary>
    private const decimal AutoMatchThreshold = 0.75m;

    /// <summary>Confidence >= this → surface as suggestion (NeedsReview).</summary>
    private const decimal SuggestionThreshold = 0.40m;

    private readonly IPlannedOccurrenceRepository _occurrenceRepo;

    public MatchingEngine(IPlannedOccurrenceRepository occurrenceRepo)
        => _occurrenceRepo = occurrenceRepo;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to match each actual transaction to a pending planned occurrence.
    /// Returns a list of suggested (ambiguous) matches for user review.
    /// Auto-matched transactions are mutated in-place.
    /// Caller is responsible for SaveChanges on both repositories.
    /// </summary>
    public async Task<List<MatchCandidate>> MatchAsync(
        IReadOnlyList<Transaction> actuals,
        int dateToleranceDays = 0,
        decimal amountToleranceFraction = 0,
        CancellationToken ct = default)
    {
        if (dateToleranceDays <= 0)
        {
            dateToleranceDays = DefaultDateToleranceDays;
        }

        if (amountToleranceFraction <= 0)
        {
            amountToleranceFraction = DefaultAmountToleranceFraction;
        }

        if (actuals.Count == 0)
        {
            return [];
        }

        // Load the window of pending occurrences covering the date range of all actuals
        var minDate = actuals.Min(t => t.EffectiveDate).AddDays(-dateToleranceDays);
        var maxDate = actuals.Max(t => t.EffectiveDate).AddDays(dateToleranceDays);

        var pendingOccurrences = await _occurrenceRepo.ListPendingAsync(minDate, maxDate, ct);

        // Build a pool of still-unmatched occurrences (an occurrence can only match once)
        var availableOccurrences = pendingOccurrences.ToList();

        var suggestions = new List<MatchCandidate>();

        foreach (var actual in actuals)
        {
            if (actual.Status is not (TransactionStatus.Posted or TransactionStatus.Pending))
            {
                continue;
            }

            var candidates = Score(actual, availableOccurrences, dateToleranceDays, amountToleranceFraction);

            if (candidates.Count == 0)
            {
                continue; // No match possible — leave as Posted
            }

            var best = candidates[0];

            if (candidates.Count == 1 && best.Confidence.Value >= SuggestionThreshold)
            {
                // Unambiguous single candidate that passed all tolerance gates — auto-match.
                // Passing both the date and amount tolerance windows is the domain's acceptance
                // criterion; requiring an additional high-confidence threshold on an already
                // unambiguous candidate would leave valid within-tolerance fuzzy matches as
                // NeedsReview, contradicting the algorithm documented above (§10.5).
                await AutoMatchAsync(actual, best.Occurrence, best.Confidence, ct);
                availableOccurrences.Remove(best.Occurrence);
            }
            else if (candidates.Count > 1 && best.Confidence.Value >= SuggestionThreshold)
            {
                // Ambiguous — multiple candidates above the suggestion threshold; flag for review
                actual.FlagNeedsReview();
                suggestions.Add(best);
            }
            // else: low confidence everywhere — leave as Posted, no action
        }

        return suggestions;
    }

    // ── Internal helpers ───────────────────────────────────────────────────────

    private static List<MatchCandidate> Score(
        Transaction actual,
        List<PlannedFlowOccurrenceView> pool,
        int dateToleranceDays,
        decimal amountToleranceFraction)
    {
        var results = new List<MatchCandidate>();

        foreach (var occ in pool)
        {
            // Account and direction must match exactly
            if (occ.AccountId != actual.AccountId)
            {
                continue;
            }

            if (occ.Direction != actual.Direction)
            {
                continue;
            }

            // Date must be within tolerance
            int dayDiff = Math.Abs(actual.EffectiveDate.DayNumber - occ.PlannedDate.DayNumber);
            if (dayDiff > dateToleranceDays)
            {
                continue;
            }

            // Amount must be within tolerance
            decimal plannedAmt = occ.PlannedAmount.Amount;
            decimal actualAmt = actual.Amount.Amount;
            if (plannedAmt == 0m)
            {
                continue;
            }

            decimal amtDiff = Math.Abs(actualAmt - plannedAmt) / plannedAmt;
            if (amtDiff > amountToleranceFraction)
            {
                continue;
            }

            var confidence = ComputeConfidence(dayDiff, dateToleranceDays, amtDiff, amountToleranceFraction);
            results.Add(new MatchCandidate(actual, occ, confidence));
        }

        // Sort by confidence descending
        results.Sort((a, b) => b.Confidence.Value.CompareTo(a.Confidence.Value));
        return results;
    }

    /// <summary>
    /// Confidence is weighted 60% amount proximity + 40% date proximity.
    /// Both dimensions contribute a score from 0 (at tolerance boundary) to 1 (exact match).
    /// </summary>
    private static ConfidenceScore ComputeConfidence(
        int dayDiff,
        int maxDays,
        decimal amtDiff,
        decimal maxAmtFraction)
    {
        decimal datePart = maxDays == 0 ? 1m : 1m - (decimal)dayDiff / maxDays;
        decimal amtPart = maxAmtFraction == 0m ? 1m : 1m - amtDiff / maxAmtFraction;

        decimal raw = 0.40m * datePart + 0.60m * amtPart;
        raw = Math.Clamp(raw, 0m, 1m);
        return new ConfidenceScore(Math.Round(raw, 4));
    }

    private async Task AutoMatchAsync(
        Transaction actual,
        PlannedFlowOccurrenceView occView,
        ConfidenceScore confidence,
        CancellationToken ct)
    {
        // Load the owning RecurringFlow aggregate to call MatchActual on the owned entity
        var flow = await _occurrenceRepo.GetOwningFlowAsync(occView.PlannedOccurrenceId, ct)
                   ?? throw new InvalidOperationException(
                       $"Owning RecurringFlow not found for occurrence {occView.PlannedOccurrenceId.Value}.");

        var occurrence = flow.Occurrences.FirstOrDefault(o => o.PlannedOccurrenceId == occView.PlannedOccurrenceId)
                         ?? throw new InvalidOperationException(
                             $"PlannedFlowOccurrence {occView.PlannedOccurrenceId.Value} not found in RecurringFlow {flow.Id}.");

        occurrence.MatchActual(
            actual.TransactionId,
            actual.Amount,
            actual.EffectiveDate,
            confidence);

        actual.Match(occView.PlannedOccurrenceId, confidence);
    }
}

/// <summary>A scored match candidate pairing an actual transaction with a planned occurrence.</summary>
public sealed record MatchCandidate(
    Transaction Actual,
    PlannedFlowOccurrenceView Occurrence,
    ConfidenceScore Confidence);
