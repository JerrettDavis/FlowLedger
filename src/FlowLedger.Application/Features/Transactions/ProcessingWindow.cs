using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.Transactions;

/// <summary>
/// How much of a tenant's history the whole-history engines (categorize, rules, duplicates, recurring suggestions, insights, query)
/// can see. <see cref="Partial"/> is true when more rows are stored than <see cref="TransactionQuota.ProcessingCap"/>, so those engines
/// consider only the newest <see cref="ProcessingCap"/> rows (#166). One honest answer for the engines that return no result object.
/// </summary>
public sealed record ProcessingWindowDto(int StoredTransactions, int ProcessingCap, int QuotaCap, bool Partial);

internal sealed class GetProcessingWindowHandler(ITransactionRepository repo)
{
    public async Task<ProcessingWindowDto> HandleAsync(CancellationToken ct = default)
    {
        var stored = await repo.CountAsync(ct);
        return new ProcessingWindowDto(stored, TransactionQuota.ProcessingCap, TransactionQuota.MaxPerTenant, stored > TransactionQuota.ProcessingCap);
    }
}
