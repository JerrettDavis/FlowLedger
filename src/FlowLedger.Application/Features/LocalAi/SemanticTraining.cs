using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.LocalAi;

/// <summary>Builds the semantic classifier's training set and scores uncategorized rows, shared by categorization and evaluation.</summary>
public static class SemanticTraining
{
    public static string TextOf(Transaction t) => DescriptorNormalizer.Normalize(t.MerchantName, t.Description);

    /// <summary>
    /// Examples = what the user categorized by hand (never auto fills, which are not evidence of intent; Transfer excluded)
    /// plus, for a learned embedder, each category's own name as a low-weight seed so an empty-ish category is still reachable.
    /// </summary>
    public static IEnumerable<LabeledText> Examples(
        IEnumerable<Transaction> manual, IReadOnlyDictionary<Guid, string> pathById, bool seedCategoryNames)
    {
        foreach (var t in manual)
        {
            if (t.CategoryId is null || !pathById.TryGetValue(t.CategoryId.Value.Value, out var path)
                || string.Equals(path, AutoCategorizer.TransferCategory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = TextOf(t);
            if (text.Length > 0)
            {
                yield return new LabeledText(text, path);
            }
        }

        if (!seedCategoryNames)
        {
            yield break;
        }

        foreach (var path in pathById.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(path, AutoCategorizer.TransferCategory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new LabeledText(path.Replace('/', ' ').ToLowerInvariant(), path, 0.5);
        }
    }

    /// <summary>Top ranked categories (best first, unfiltered by confidence) per distinct normalised text.</summary>
    public static async Task<Dictionary<string, IReadOnlyList<Prediction>>> RankAsync(
        ITextEmbedder embedder, SemanticCategoryClassifier classifier, IReadOnlyCollection<string> texts, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<Prediction>>(StringComparer.Ordinal);
        var distinct = texts.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return result;
        }

        var vectors = await embedder.EmbedAsync(distinct, EmbedKind.Passage, ct);
        for (var i = 0; i < distinct.Count; i++)
        {
            var ranked = classifier.Rank(vectors[i], 3);
            if (ranked.Count > 0)
            {
                result[distinct[i]] = ranked;
            }
        }

        return result;
    }
}
