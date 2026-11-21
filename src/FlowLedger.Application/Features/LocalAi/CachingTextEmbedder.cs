using System.Collections.Concurrent;
using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.LocalAi;

/// <summary>
/// Bounded in-memory memo in front of any embedder (keyed by kind + text), so repeated merchants are embedded once.
/// Holds only derived vectors of short descriptors; nothing is persisted and nothing is tenant-addressable.
/// </summary>
public sealed class CachingTextEmbedder(ITextEmbedder inner, int capacity = 100_000) : ITextEmbedder
{
    private readonly ConcurrentDictionary<(EmbedKind, string), float[]> _cache = new();

    public string ModelId => inner.ModelId;
    public int Dimensions => inner.Dimensions;
    public bool IsSemantic => inner.IsSemantic;
    public double DefaultSameMerchantThreshold => inner.DefaultSameMerchantThreshold;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Passage, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        var missing = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (_cache.TryGetValue((kind, texts[i]), out var hit))
            {
                result[i] = hit;
            }
            else
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            var distinct = missing.Select(i => texts[i]).Distinct(StringComparer.Ordinal).ToList();
            var vectors = await inner.EmbedAsync(distinct, kind, ct);
            if (_cache.Count + distinct.Count > capacity)
            {
                _cache.Clear(); // simple, safe eviction: the working set is small and re-embedding is cheap
            }

            var byText = new Dictionary<string, float[]>(StringComparer.Ordinal);
            for (var i = 0; i < distinct.Count; i++)
            {
                byText[distinct[i]] = vectors[i];
                _cache[(kind, distinct[i])] = vectors[i];
            }

            foreach (var i in missing)
            {
                result[i] = byText[texts[i]];
            }
        }

        return result;
    }
}
