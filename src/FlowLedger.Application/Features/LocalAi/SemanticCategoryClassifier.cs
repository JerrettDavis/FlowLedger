using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;

namespace FlowLedger.Application.Features.LocalAi;

/// <summary>One labelled descriptor (already normalised) used as a nearest-neighbour example.</summary>
public sealed record LabeledText(string Text, string Label, double Weight = 1.0);

/// <summary>
/// Embedding k-nearest-neighbour category classifier ("SetFit-lite": a frozen sentence embedder + a non-parametric head).
/// It needs no training run and no stored model: it is rebuilt per request from the user's own manual categories, so it
/// learns a correction the moment it is made. One vote per distinct merchant (weight capped), so a merchant seen 200 times
/// cannot drown the long tail. Pure apart from the embedder call.
/// </summary>
public sealed class SemanticCategoryClassifier
{
    public const int MinExamples = 10;
    public const double MaxMerchantWeight = 3;

    private readonly float[][] _vectors;
    private readonly string[] _labels;
    private readonly double[] _weights;
    private readonly int _k;

    private SemanticCategoryClassifier(float[][] vectors, string[] labels, double[] weights, int k)
    {
        _vectors = vectors;
        _labels = labels;
        _weights = weights;
        _k = Math.Max(1, k);
    }

    public int ExampleCount => _vectors.Length;

    /// <summary>Builds a classifier, or null when there is too little (or too uniform) labelled data to be trustworthy.</summary>
    public static async Task<SemanticCategoryClassifier?> TrainAsync(
        ITextEmbedder embedder, IEnumerable<LabeledText> examples, int neighbours, CancellationToken ct = default)
    {
        // One example per distinct text: its majority label; weight grows with repeat count but is capped.
        var merged = examples
            .Where(e => e.Text.Length > 0)
            .GroupBy(e => e.Text, StringComparer.Ordinal)
            .Select(g =>
            {
                var top = g.GroupBy(e => e.Label).OrderByDescending(x => x.Sum(e => e.Weight)).First();
                return new LabeledText(g.Key, top.Key, Math.Min(MaxMerchantWeight, top.Sum(e => e.Weight)));
            })
            .ToList();

        if (merged.Count < MinExamples || merged.Select(m => m.Label).Distinct().Count() < 2)
        {
            return null;
        }

        var vectors = await embedder.EmbedAsync(merged.Select(m => m.Text).ToList(), EmbedKind.Passage, ct);
        return new SemanticCategoryClassifier(vectors, merged.Select(m => m.Label).ToArray(), merged.Select(m => m.Weight).ToArray(), neighbours);
    }

    /// <summary>Ranked label candidates (best first) for an embedded query; empty when nothing is near enough to say anything.</summary>
    public IReadOnlyList<Prediction> Rank(float[] query, int top = 3)
    {
        if (_vectors.Length == 0)
        {
            return [];
        }

        var sims = new (float Sim, int Index)[_vectors.Length];
        for (var i = 0; i < _vectors.Length; i++)
        {
            sims[i] = (VectorMath.Dot(query, _vectors[i]), i);
        }

        var nearest = sims.OrderByDescending(s => s.Sim).Take(_k).Where(s => s.Sim > 0.05f).ToList();
        if (nearest.Count == 0)
        {
            return [];
        }

        // Sharpened similarity vote: close neighbours dominate far ones.
        var score = new Dictionary<string, double>();
        var best = new Dictionary<string, double>();
        foreach (var (sim, index) in nearest)
        {
            var label = _labels[index];
            score[label] = score.GetValueOrDefault(label) + _weights[index] * Math.Pow(sim, 3);
            best[label] = Math.Max(best.GetValueOrDefault(label), sim);
        }

        var total = score.Values.Sum();
        if (total <= 0)
        {
            return [];
        }

        // confidence = vote share x how close the best supporting neighbour really is (a clear majority of distant
        // neighbours is not a confident answer).
        return score
            .Select(kv => new Prediction(kv.Key, kv.Value / total * Math.Clamp((best[kv.Key] - 0.15) / 0.45, 0, 1)))
            .OrderByDescending(p => p.Probability)
            .Take(top)
            .ToList();
    }

    public Prediction? Predict(float[] query) => Rank(query, 1).FirstOrDefault();
}

/// <summary>Combines the lexical naive-Bayes prediction with the semantic one into a single confidence.</summary>
public static class PredictionEnsemble
{
    /// <summary>
    /// Agreement is strong evidence (independent-ish signals: noisy-OR); disagreement keeps one at a discount
    /// (the more confident, or the semantic one when <c>preferSemantic</c>).
    /// </summary>
    public static Prediction? Combine(Prediction? bayes, Prediction? semantic, bool preferSemantic = false)
    {
        if (bayes is null)
        {
            return semantic;
        }

        if (semantic is null)
        {
            return bayes;
        }

        if (string.Equals(bayes.Label, semantic.Label, StringComparison.OrdinalIgnoreCase))
        {
            return new Prediction(bayes.Label, 1 - (1 - bayes.Probability) * (1 - semantic.Probability));
        }

        // A learned embedder generalises to unseen merchants far better than word counts, and naive-Bayes probabilities are
        // poorly calibrated, so on disagreement the semantic answer wins unless Bayes is clearly more confident.
        var winner = preferSemantic
            ? (bayes.Probability > semantic.Probability + 0.25 ? bayes : semantic)
            : (bayes.Probability >= semantic.Probability ? bayes : semantic);
        return winner with { Probability = winner.Probability * 0.85 };
    }
}
