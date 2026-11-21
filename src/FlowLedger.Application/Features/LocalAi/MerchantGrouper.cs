using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.LocalAi;

/// <summary>One distinct raw merchant string and how often it occurs.</summary>
public sealed record MerchantOccurrence(string Raw, int Count);

/// <summary>A set of raw merchant strings that very likely name one merchant.</summary>
public sealed record MerchantGroup(string Canonical, IReadOnlyList<MerchantOccurrence> Members, double MinSimilarity)
{
    public int TotalCount => Members.Sum(m => m.Count);
}

/// <summary>
/// Groups merchant names that are the same business written differently ("STARBUCKS #1042", "Starbucks Store 55",
/// "SQ *STARBUCKS"). Leader clustering: strongest (most frequent) name first, a name joins the first leader it is
/// close enough to. Comparing against the LEADER (not any member) stops chains from drifting into unrelated names.
/// Suggest-only: nothing is renamed; the caller turns an accepted group into merchant aliases.
/// </summary>
public static class MerchantGrouper
{
    public static async Task<IReadOnlyList<MerchantGroup>> GroupAsync(
        ITextEmbedder embedder, IReadOnlyList<MerchantOccurrence> merchants, double threshold, CancellationToken ct = default)
    {
        var items = merchants
            .Select(m => (m.Raw, m.Count, Norm: DescriptorNormalizer.Normalize(m.Raw, null)))
            .Where(m => m.Norm.Length >= 3)
            .OrderByDescending(m => m.Count).ThenBy(m => m.Raw, StringComparer.Ordinal)
            .ToList();
        if (items.Count < 2)
        {
            return [];
        }

        var vectors = await embedder.EmbedAsync(items.Select(i => i.Norm).ToList(), EmbedKind.Passage, ct);
        var leaders = new List<int>();
        var members = new List<List<(int Index, float Sim)>>();
        for (var i = 0; i < items.Count; i++)
        {
            var placed = false;
            for (var l = 0; l < leaders.Count; l++)
            {
                var sim = i == leaders[l] ? 1f : VectorMath.Dot(vectors[i], vectors[leaders[l]]);
                var sameNorm = items[i].Norm == items[leaders[l]].Norm;
                if (sameNorm || sim >= threshold)
                {
                    members[l].Add((i, sameNorm ? 1f : sim));
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                leaders.Add(i);
                members.Add([(i, 1f)]);
            }
        }

        return members
            .Where(g => g.Count >= 2)
            .Select(g => new MerchantGroup(
                items[g[0].Index].Raw,
                g.Select(x => new MerchantOccurrence(items[x.Index].Raw, items[x.Index].Count)).ToList(),
                g.Min(x => (double)x.Sim)))
            .OrderByDescending(g => g.TotalCount)
            .ToList();
    }
}
