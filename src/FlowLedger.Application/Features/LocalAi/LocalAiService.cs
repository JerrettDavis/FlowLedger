using System.Diagnostics;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.LocalAi;

public sealed record SimilarTransactionDto(
    Guid Id, DateOnly Date, string? Merchant, string Description, decimal Amount, string Direction, string? CategoryPath, double Similarity);

public sealed record MerchantGroupDto(string Canonical, IReadOnlyList<MerchantOccurrence> Members, int TotalCount, double MinSimilarity, string SuggestedName);

public sealed record MerchantGroupsResult(string Embedder, bool Semantic, double Threshold, IReadOnlyList<MerchantGroupDto> Groups);

/// <summary>Precision/coverage at one confidence cut-off.</summary>
public sealed record EvalPoint(double Threshold, int Predicted, int Correct, double Coverage, double Precision);

public sealed record EvalMethod(string Name, double Accuracy, IReadOnlyList<EvalPoint> Curve);

public sealed record EvaluationResult(
    string Embedder, bool Semantic, int Merchants, int Labels, int Folds, long Milliseconds, string Note, IReadOnlyList<EvalMethod> Methods);

/// <summary>
/// Read-only helpers built on the in-process embedder: "what else looks like this transaction?", "which merchant names are
/// really one merchant?" and "how good are the classifiers on YOUR data?". Nothing here writes.
/// </summary>
public sealed class LocalAiService(
    ICategorizationStore store,
    ICategoryRepository categories,
    IMerchantAliasStore aliases,
    ITextEmbedder embedder,
    LocalAiOptions options)
{
    private const int Max = TransactionQuota.ProcessingCap;

    public LocalAiRuntimeStatus Status() => embedder is ILocalAiRuntime runtime
        ? runtime.GetStatus()
        : new LocalAiRuntimeStatus(
            "Custom", embedder.ModelId, embedder.Dimensions, embedder.IsSemantic, true, null, embedder.ModelId,
            false, false, false, null, string.Empty, false, []);

    private static bool IsReal(Transaction t) => t.Status.IsReal();

    /// <summary>The transactions whose descriptor is most similar to the given one (merchant-name similarity, not amount).</summary>
    public async Task<IReadOnlyList<SimilarTransactionDto>> SimilarAsync(Guid transactionId, int top, CancellationToken ct = default)
    {
        var target = await store.FindTrackedAsync(transactionId, ct) ?? throw new KeyNotFoundException("Transaction not found.");
        top = Math.Clamp(top, 1, 50);
        var all = (await store.LoadTrackedAsync(Max, ct)).Where(IsReal).Where(t => t.Id != target.Id).ToList();
        var paths = (await categories.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Path.Value);

        var texts = all.Select(SemanticTraining.TextOf).ToList();
        var targetText = SemanticTraining.TextOf(target);
        if (targetText.Length == 0 || all.Count == 0)
        {
            return [];
        }

        var distinct = texts.Append(targetText).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Take(options.MaxTextsPerRequest + 1).ToList();
        var vectors = await embedder.EmbedAsync(distinct, EmbedKind.Passage, ct);
        var vecByText = distinct.Zip(vectors).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal);
        if (!vecByText.TryGetValue(targetText, out var q))
        {
            return [];
        }

        return all
            .Select((t, i) => (t, sim: vecByText.TryGetValue(texts[i], out var v) ? VectorMath.Dot(q, v) : -1f))
            .Where(x => x.sim > 0.2f)
            .OrderByDescending(x => x.sim).ThenByDescending(x => x.t.EffectiveDate)
            .Take(top)
            .Select(x =>
            {
                var d = TransactionMapper.ToDto(x.t);
                return new SimilarTransactionDto(
                    d.Id, d.EffectiveDate, d.MerchantName, d.Description, d.Amount, d.Direction,
                    d.CategoryId is { } c ? paths.GetValueOrDefault(c) : null, Math.Round(x.sim, 3));
            })
            .ToList();
    }

    /// <summary>Distinct merchant strings that likely name the same business. Already-aliased names are left out.</summary>
    public async Task<MerchantGroupsResult> MerchantGroupsAsync(double? threshold, CancellationToken ct = default)
    {
        var t = threshold is > 0 and < 1 ? threshold.Value
            : options.SameMerchantThreshold > 0 ? options.SameMerchantThreshold
            : embedder.DefaultSameMerchantThreshold;

        var real = (await store.LoadTrackedAsync(Max, ct)).Where(IsReal).ToList();
        var aliased = (await aliases.ListAsync(ct)).Select(a => a.Alias.ToUpperInvariant()).ToHashSet();
        var counts = real
            .Select(x => string.IsNullOrWhiteSpace(x.MerchantName) ? x.Description : x.MerchantName!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .GroupBy(s => s.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new MerchantOccurrence(g.Key, g.Count()))
            .OrderByDescending(m => m.Count)
            .Take(options.MaxTextsPerRequest)
            .ToList();

        var groups = await MerchantGrouper.GroupAsync(embedder, counts, t, ct);
        var dtos = groups
            .Where(g => !g.Members.All(m => aliased.Contains(m.Raw.ToUpperInvariant())))
            .Take(100)
            .Select(g => new MerchantGroupDto(g.Canonical, g.Members, g.TotalCount, Math.Round(g.MinSimilarity, 3), PrettyName(g.Canonical)))
            .ToList();
        return new MerchantGroupsResult(embedder.ModelId, embedder.IsSemantic, Math.Round(t, 3), dtos);
    }

    /// <summary>
    /// Honest accuracy check: group k-fold by merchant, so each fold's test merchants were never seen in training (the only case
    /// where a model adds anything over the learned-merchant step). Compares naive Bayes, the semantic kNN and their ensemble.
    /// </summary>
    public async Task<EvaluationResult> EvaluateAsync(int folds, CancellationToken ct = default)
    {
        folds = Math.Clamp(folds, 2, 10);
        var sw = Stopwatch.StartNew();
        var cats = await categories.ListAsync(ct);
        var pathById = cats.ToDictionary(c => c.Id, c => c.Path.Value);
        var manual = (await store.LoadTrackedAsync(Max, ct))
            .Where(IsReal).Where(t => t.CategorySource == CategorySource.Manual && t.CategoryId is not null).ToList();

        var labelled = manual
            .Select(t => (t, text: SemanticTraining.TextOf(t), path: pathById.GetValueOrDefault(t.CategoryId!.Value.Value)))
            .Where(x => x.text.Length > 0 && x.path is not null
                && !string.Equals(x.path, AutoCategorizer.TransferCategory, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Bound the work (kNN is O(n^2) over the held-out folds): the same per-request cap as every other helper.
        var capped = labelled.Select(x => x.text).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
            .Take(options.MaxTextsPerRequest).ToHashSet(StringComparer.Ordinal);
        labelled = labelled.Where(x => capped.Contains(x.text)).ToList();
        var merchants = capped.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var labelCount = labelled.Select(x => x.path).Distinct().Count();
        if (merchants.Count < 30 || labelCount < 3)
        {
            return new EvaluationResult(embedder.ModelId, embedder.IsSemantic, merchants.Count, labelCount, folds, sw.ElapsedMilliseconds,
                "Not enough hand-categorized merchants yet (need about 30 across 3+ categories) for a meaningful measurement.", []);
        }

        // Deterministic fold assignment by merchant text, so a merchant is never in both train and test.
        int FoldOf(string text) => (int)(StableHash(text) % (uint)folds);
        var thresholds = new[] { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 };
        var bayesHits = new List<(double P, bool Ok)>();
        var semHits = new List<(double P, bool Ok)>();
        var ensHits = new List<(double P, bool Ok)>();
        var total = 0;

        for (var f = 0; f < folds; f++)
        {
            ct.ThrowIfCancellationRequested();
            var train = labelled.Where(x => FoldOf(x.text) != f).ToList();
            var test = labelled.Where(x => FoldOf(x.text) == f).ToList();
            if (test.Count == 0)
            {
                continue;
            }

            var bayes = NaiveBayesCategoryPredictor.Train(train.Select(x =>
                new TrainingExample(x.t.MerchantName, x.t.Description, x.t.Direction.ToString(), x.t.Amount.Amount, x.path!)));
            var sem = await SemanticCategoryClassifier.TrainAsync(
                embedder, train.Select(x => new LabeledText(x.text, x.path!)), options.Neighbours, ct);
            var vectors = await embedder.EmbedAsync(test.Select(x => x.text).Distinct(StringComparer.Ordinal).ToList(), EmbedKind.Passage, ct);
            var vecByText = test.Select(x => x.text).Distinct(StringComparer.Ordinal).Zip(vectors).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal);

            foreach (var x in test)
            {
                total++;
                var b = bayes?.Predict(x.t.MerchantName, x.t.Description, x.t.Direction.ToString(), x.t.Amount.Amount);
                var s = sem?.Predict(vecByText[x.text]);
                var e = PredictionEnsemble.Combine(b, s, embedder.IsSemantic);
                if (b is not null) { bayesHits.Add((b.Probability, b.Label == x.path)); }
                if (s is not null) { semHits.Add((s.Probability, s.Label == x.path)); }
                if (e is not null) { ensHits.Add((e.Probability, e.Label == x.path)); }
            }
        }

        EvalMethod Method(string name, List<(double P, bool Ok)> hits) => new(
            name,
            total == 0 ? 0 : Math.Round(hits.Count(h => h.Ok) / (double)total, 3),
            thresholds.Select(th =>
            {
                var sel = hits.Where(h => h.P >= th).ToList();
                var ok = sel.Count(h => h.Ok);
                return new EvalPoint(th, sel.Count, ok, total == 0 ? 0 : Math.Round(sel.Count / (double)total, 3),
                    sel.Count == 0 ? 0 : Math.Round(ok / (double)sel.Count, 3));
            }).ToList());

        var note = embedder.IsSemantic
            ? "Held-out merchants (group k-fold). Precision is measured on merchants the model had never seen."
            : "Using the lexical fallback embedder (no model installed): install a model on this page to see the semantic gain.";
        return new EvaluationResult(embedder.ModelId, embedder.IsSemantic, merchants.Count, labelCount, folds, sw.ElapsedMilliseconds, note,
            [Method("Naive Bayes (current)", bayesHits), Method("Embedding kNN", semHits), Method("Ensemble", ensHits)]);
    }

    private static string PrettyName(string raw)
    {
        var n = DescriptorNormalizer.Normalize(raw, null);
        return n.Length == 0 ? raw : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n);
    }

    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s)
        {
            h = (h ^ ch) * 16777619;
        }

        return h;
    }
}
