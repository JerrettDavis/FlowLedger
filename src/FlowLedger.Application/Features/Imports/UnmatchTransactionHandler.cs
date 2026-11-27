using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Imports;

/// <summary>
/// Removes a match link between an actual transaction and a planned occurrence.
/// </summary>
public sealed class UnmatchTransactionHandler
{
    private readonly ITransactionRepository _txRepo;
    private readonly IPlannedOccurrenceRepository _occurrenceRepo;

    public UnmatchTransactionHandler(
        ITransactionRepository txRepo,
        IPlannedOccurrenceRepository occurrenceRepo)
    {
        _txRepo = txRepo;
        _occurrenceRepo = occurrenceRepo;
    }

    public async Task<MatchResultDto> HandleAsync(
        UnmatchTransactionCommand command,
        CancellationToken ct = default)
    {
        var tx = await _txRepo.GetByIdAsync(TransactionId.From(command.TransactionId), ct)
                 ?? throw new UnmatchFailedException($"Transaction {command.TransactionId} not found.");

        var previousOccId = tx.Unmatch();

        if (previousOccId.HasValue)
        {
            var flow = await _occurrenceRepo.GetOwningFlowAsync(previousOccId.Value, ct);
            if (flow is not null)
            {
                var occ = flow.Occurrences.FirstOrDefault(
                    o => o.PlannedOccurrenceId == previousOccId.Value);
                occ?.Unmatch();
            }
        }

        await _occurrenceRepo.SaveChangesAsync(ct);
        await _txRepo.SaveChangesAsync(ct);

        return new MatchResultDto(tx.Id, null, tx.Status.ToString(), null, null, null, null);
    }
}
