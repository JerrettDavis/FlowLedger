using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Imports;

/// <summary>
/// Manually links an actual transaction to a planned occurrence.
/// </summary>
public sealed class MatchTransactionHandler
{
    private readonly ITransactionRepository _txRepo;
    private readonly IPlannedOccurrenceRepository _occurrenceRepo;

    public MatchTransactionHandler(
        ITransactionRepository txRepo,
        IPlannedOccurrenceRepository occurrenceRepo)
    {
        _txRepo = txRepo;
        _occurrenceRepo = occurrenceRepo;
    }

    public async Task<MatchResultDto> HandleAsync(
        MatchTransactionCommand command,
        CancellationToken ct = default)
    {
        var tx = await _txRepo.GetByIdAsync(TransactionId.From(command.TransactionId), ct)
                 ?? throw new InvalidOperationException($"Transaction {command.TransactionId} not found.");

        var flow = await _occurrenceRepo.GetOwningFlowAsync(
                       PlannedOccurrenceId.From(command.PlannedOccurrenceId), ct)
                   ?? throw new InvalidOperationException(
                       $"PlannedOccurrence {command.PlannedOccurrenceId} not found.");

        var occurrence = flow.Occurrences.FirstOrDefault(
                             o => o.PlannedOccurrenceId == PlannedOccurrenceId.From(command.PlannedOccurrenceId))
                         ?? throw new InvalidOperationException(
                             $"PlannedOccurrence {command.PlannedOccurrenceId} not found in flow.");

        // Manual match uses Certain confidence
        var confidence = ConfidenceScore.Certain;
        occurrence.MatchActual(tx.TransactionId, tx.Amount, tx.EffectiveDate, confidence);
        tx.Match(occurrence.PlannedOccurrenceId, confidence);

        await _occurrenceRepo.SaveChangesAsync(ct);
        await _txRepo.SaveChangesAsync(ct);

        return MapResult(tx, occurrence);
    }

    private static MatchResultDto MapResult(Transaction tx, PlannedFlowOccurrence occ) =>
        new(tx.Id, occ.Id,
            tx.Status.ToString(),
            occ.Status.ToString(),
            occ.MatchConfidence?.Value,
            occ.AmountVariance?.Amount,
            occ.DateVarianceDays);
}
