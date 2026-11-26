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
    private const int Max = TransactionQuota.ProcessingCap;

    public async Task<EnrichmentResult> RunAsync(CancellationToken ct = default)
    {
        int tagged = 0, named = 0;
        var changedRows = new List<FlowLedger.Domain.Aggregates.Transaction>();
        foreach (var t in await store.LoadAsync(Max, ct))
        {
            if (!t.Status.IsReal())
            {
                continue;
            }

            var touched = false;
            if (t.SetSuggestedName(MerchantNameSuggester.Suggest(t.Description)))
            {
                named++;
                touched = true;
            }

            var tags = AutoTagger.Suggest(t.MerchantName, t.Description, t.Direction.ToString(), t.Amount.Amount, t.Status);
            if (t.ApplyAutoTags(tags, AutoTagger.Vocabulary))
            {
                tagged++;
                touched = true;
            }

            if (touched)
            {
                changedRows.Add(t);
            }
        }

        if (tagged + named > 0)
        {
            await store.SaveChangedAsync(changedRows, ct);
        }

        return new EnrichmentResult(tagged, named);
    }
}
