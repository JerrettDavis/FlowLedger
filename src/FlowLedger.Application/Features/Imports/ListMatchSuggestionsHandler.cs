using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Imports;

/// <summary>
/// Returns match suggestions (transactions with NeedsReview status) for the current tenant.
/// </summary>
internal sealed class ListMatchSuggestionsHandler
{
    private readonly ITransactionRepository _txRepo;
    private readonly IPlannedOccurrenceRepository _occurrenceRepo;

    public ListMatchSuggestionsHandler(
        ITransactionRepository txRepo,
        IPlannedOccurrenceRepository occurrenceRepo)
    {
        _txRepo = txRepo;
        _occurrenceRepo = occurrenceRepo;
    }

    public async Task<IReadOnlyList<MatchSuggestionDto>> HandleAsync(CancellationToken ct = default)
    {
        // Get NeedsReview transactions
        var needsReview = await _txRepo.ListAsync(
            take: 200,
            ct: ct);

        var candidates = needsReview
            .Where(t => t.Status == TransactionStatus.NeedsReview)
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var minDate = candidates.Min(t => t.EffectiveDate).AddDays(-MatchingEngine.DefaultDateToleranceDays);
        var maxDate = candidates.Max(t => t.EffectiveDate).AddDays(MatchingEngine.DefaultDateToleranceDays);

        var pendingOccurrences = await _occurrenceRepo.ListPendingAsync(minDate, maxDate, ct);

        var results = new List<MatchSuggestionDto>();

        foreach (var tx in candidates)
        {
            // Find best pending occurrence
            var best = pendingOccurrences
                .Where(o => o.AccountId == tx.AccountId && o.Direction == tx.Direction)
                .OrderBy(o => Math.Abs(o.PlannedDate.DayNumber - tx.EffectiveDate.DayNumber))
                .FirstOrDefault();

            if (best is null)
            {
                continue;
            }

            int dayDiff = Math.Abs(tx.EffectiveDate.DayNumber - best.PlannedDate.DayNumber);
            decimal amtDiff = best.PlannedAmount.Amount == 0m ? 0m :
                Math.Abs(tx.Amount.Amount - best.PlannedAmount.Amount) / best.PlannedAmount.Amount;

            decimal datePart = MatchingEngine.DefaultDateToleranceDays == 0 ? 1m :
                1m - (decimal)dayDiff / MatchingEngine.DefaultDateToleranceDays;
            decimal amtPart = MatchingEngine.DefaultAmountToleranceFraction == 0m ? 1m :
                1m - amtDiff / MatchingEngine.DefaultAmountToleranceFraction;
            decimal confidence = Math.Clamp(0.40m * datePart + 0.60m * amtPart, 0m, 1m);

            results.Add(new MatchSuggestionDto(
                tx.Id,
                best.PlannedOccurrenceId.Value,
                confidence,
                tx.Amount.Amount,
                best.PlannedAmount.Amount,
                tx.Amount.Currency.Code,
                tx.EffectiveDate,
                best.PlannedDate,
                tx.Description,
                best.RecurringFlowName));
        }

        return results.AsReadOnly();
    }
}
