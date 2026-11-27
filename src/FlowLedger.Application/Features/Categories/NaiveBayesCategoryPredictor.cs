using System.Text.RegularExpressions;

namespace FlowLedger.Application.Features.Categories;

/// <summary>One labelled transaction used to train the model.</summary>
internal sealed record TrainingExample(string? Merchant, string? Description, string Direction, decimal Amount, string Label);

internal sealed record Prediction(string Label, double Probability);

/// <summary>
/// Small in-process multinomial naive Bayes text classifier over a transaction's merchant/description
/// (words + character 3-grams, digits stripped) plus direction and amount bucket. Pure, deterministic, no dependencies.
/// Trained fresh from the user's own manual categories, so it needs no stored model.
/// </summary>
internal sealed class NaiveBayesCategoryPredictor
{
    public const int MinExamples = 20;
    public const int MinPerLabel = 2;

    /// <summary>Exponent for score tempering (see <see cref="Predict"/>); 0 = raw naive Bayes. 0.5 chosen by 5-fold CV on real data.</summary>
    private const double TemperExponent = 0.5;

    private readonly Dictionary<string, Dictionary<string, int>> _counts = [];   // label -> feature -> count
    private readonly Dictionary<string, int> _totals = [];                        // label -> total feature count
    private readonly Dictionary<string, int> _docs = [];                          // label -> examples
    private readonly HashSet<string> _vocab = [];
    private int _docTotal;

    private NaiveBayesCategoryPredictor() { }

    /// <summary>Trains a model, or returns null when there is too little (or too uniform) data to be trustworthy.</summary>
    public static NaiveBayesCategoryPredictor? Train(IEnumerable<TrainingExample> examples)
    {
        var all = examples.Where(e => Features(e.Merchant, e.Description, e.Direction, e.Amount).Count > 0).ToList();
        var eligible = all.GroupBy(e => e.Label).Where(g => g.Count() >= MinPerLabel).Select(g => g.Key).ToHashSet();
        var data = all.Where(e => eligible.Contains(e.Label)).ToList();
        if (data.Count < MinExamples || eligible.Count < 2)
        {
            return null;
        }

        var m = new NaiveBayesCategoryPredictor();
        foreach (var e in data)
        {
            m._docs[e.Label] = m._docs.GetValueOrDefault(e.Label) + 1;
            m._docTotal++;
            if (!m._counts.TryGetValue(e.Label, out var c))
            {
                m._counts[e.Label] = c = [];
            }

            foreach (var f in Features(e.Merchant, e.Description, e.Direction, e.Amount))
            {
                c[f] = c.GetValueOrDefault(f) + 1;
                m._totals[e.Label] = m._totals.GetValueOrDefault(e.Label) + 1;
                m._vocab.Add(f);
            }
        }

        return m;
    }

    /// <summary>Most likely label with its normalised probability; null when the text carries no usable signal.</summary>
    public Prediction? Predict(string? merchant, string? description, string direction, decimal amount)
    {
        var feats = Features(merchant, description, direction, amount);
        if (feats.Count == 0)
        {
            return null;
        }

        var v = _vocab.Count;
        var logs = new Dictionary<string, double>();
        foreach (var label in _docs.Keys)
        {
            var lp = Math.Log((double)_docs[label] / _docTotal);
            var counts = _counts[label];
            var denom = _totals[label] + v;
            foreach (var f in feats)
            {
                lp += Math.Log((counts.GetValueOrDefault(f) + 1.0) / denom);
            }

            logs[label] = lp;
        }

        // Naive Bayes multiplies one factor per feature, so its raw probabilities saturate toward 0/1 (badly overconfident
        // on sparse data). Dividing the log-scores by feature_count^Exponent tempers them so the probability tracks
        // per-feature evidence; the threshold is then chosen from cross-validated precision, not guessed.
        var temper = Math.Pow(feats.Count, TemperExponent);
        var max = logs.Values.Max();
        var sum = logs.Values.Sum(x => Math.Exp((x - max) / temper));
        var best = logs.OrderByDescending(kv => kv.Value).First();
        return new Prediction(best.Key, 1.0 / sum);
    }

    /// <summary>K-fold evaluation: share of rows predicted at/above <paramref name="threshold"/>, and how often those were right.</summary>
    public static (double Coverage, double PrecisionAtThreshold) CrossValidate(
        IReadOnlyList<TrainingExample> examples, int folds, double threshold)
    {
        int predicted = 0, correct = 0, total = 0;
        for (var k = 0; k < folds; k++)
        {
            var train = examples.Where((_, i) => i % folds != k);
            var test = examples.Where((_, i) => i % folds == k).ToList();
            var model = Train(train);
            total += test.Count;
            if (model is null)
            {
                continue;
            }

            foreach (var e in test)
            {
                var p = model.Predict(e.Merchant, e.Description, e.Direction, e.Amount);
                if (p is not null && p.Probability >= threshold)
                {
                    predicted++;
                    if (p.Label == e.Label)
                    {
                        correct++;
                    }
                }
            }
        }

        return (total == 0 ? 0 : (double)predicted / total, predicted == 0 ? 0 : (double)correct / predicted);
    }

    private static readonly Regex NonLetters = new(@"[^a-z&+\s]", RegexOptions.Compiled);

    private static List<string> Features(string? merchant, string? description, string direction, decimal amount)
    {
        var text = NonLetters.Replace(string.Join(' ', merchant, description).ToLowerInvariant(), " ");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var f = new List<string>();
        foreach (var w in words)
        {
            f.Add("w:" + w);
            var padded = "^" + w + "$";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                f.Add(string.Concat("c:", padded.AsSpan(i, 3)));
            }
        }

        f.Add("d:" + direction.ToLowerInvariant());
        f.Add("a:" + AmountBucket(Math.Abs(amount)));
        return f;
    }

    private static string AmountBucket(decimal a) => a switch
    {
        < 10 => "xs",
        < 30 => "s",
        < 100 => "m",
        < 500 => "l",
        < 2000 => "xl",
        _ => "xxl",
    };
}
