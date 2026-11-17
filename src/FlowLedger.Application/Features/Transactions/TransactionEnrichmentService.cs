using FlowLedger.Application.Features.Categories;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Transactions;

public sealed record EnrichmentResult(int Tagged, int Named);

/// <summary>
/// Ingestion enrichment: derives a suggested display name and automatic tags for every real transaction.
/// Idempotent, never touches the user's category or merchant alias, and only manages tags in <see cref="AutoTagger.Vocabulary"/>.
/// </summary>
public sealed class TransactionEnrichmentService(ICategorizationStore store)
{
    private const int Max = 50_000;

    public async Task<EnrichmentResult> RunAsync(CancellationToken ct = default)
    {
        int tagged = 0, named = 0;
        foreach (var t in await store.LoadTrackedAsync(Max, ct))
        {
            if (t.Status is TransactionStatus.Planned or TransactionStatus.Skipped)
            {
                continue;
            }

            if (t.SetSuggestedName(MerchantNameSuggester.Suggest(t.Description)))
            {
                named++;
            }

            var tags = AutoTagger.Suggest(t.MerchantName, t.Description, t.Direction.ToString(), t.Amount.Amount, t.Status);
            if (t.ApplyAutoTags(tags, AutoTagger.Vocabulary))
            {
                tagged++;
            }
        }

        if (tagged + named > 0)
        {
            await store.SaveAsync(ct);
        }

        return new EnrichmentResult(tagged, named);
    }
}
