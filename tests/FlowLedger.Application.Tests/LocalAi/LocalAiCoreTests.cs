using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Categories;
using FlowLedger.Application.Features.LocalAi;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.LocalAi;

public class LocalAiCoreTests
{
    private static readonly HashingTextEmbedder Hash = new();

    [Theory]
    [InlineData("SQ *BLUE BOTTLE COFFEE #1042 OAKLAND CA", "blue bottle coffee oakland")]
    [InlineData("TST* Taco Shop 2201", "taco shop")]
    [InlineData("AMAZON.COM*2K4LP9 AMZN.COM/BILL WA", "amazon amzn bill")]
    public void Normalizer_strips_prefixes_digits_and_punctuation(string raw, string expected)
        => DescriptorNormalizer.Normalize(raw, null).Should().Be(expected);

    [Fact]
    public void Normalizer_prefers_merchant_over_description_and_folds_accents()
        => DescriptorNormalizer.Normalize("Café Zoë", "ignored 123").Should().Be("cafe zoe");

    [Fact]
    public async Task Hashing_embedder_returns_unit_vectors_and_is_deterministic()
    {
        var a = (await Hash.EmbedAsync(["starbucks coffee"]))[0];
        var b = (await Hash.EmbedAsync(["starbucks coffee"]))[0];
        a.Should().Equal(b);
        VectorMath.Dot(a, a).Should().BeApproximately(1f, 1e-4f);
    }

    [Fact]
    public async Task Hashing_embedder_ranks_spelling_variants_above_unrelated_names()
    {
        var v = await Hash.EmbedAsync(["starbucks", "starbuck coffee", "shell oil"]);
        VectorMath.Dot(v[0], v[1]).Should().BeGreaterThan(VectorMath.Dot(v[0], v[2]));
    }

    [Fact]
    public async Task Caching_embedder_embeds_each_text_once_and_keeps_order()
    {
        var counting = new CountingEmbedder();
        var cached = new CachingTextEmbedder(counting);
        var first = await cached.EmbedAsync(["a", "b", "a"]);
        var second = await cached.EmbedAsync(["b", "c"]);
        counting.Embedded.Should().BeEquivalentTo(["a", "b", "c"]);
        first[0].Should().Equal(first[2]);
        second[0].Should().Equal(first[1]);
    }

    [Fact]
    public async Task Classifier_needs_enough_labelled_distinct_merchants()
    {
        var few = Enumerable.Range(0, 5).Select(i => new LabeledText("m" + i, i % 2 == 0 ? "A" : "B"));
        (await SemanticCategoryClassifier.TrainAsync(Hash, few, 5)).Should().BeNull();

        var oneLabel = Enumerable.Range(0, 20).Select(i => new LabeledText("merchant " + i, "A"));
        (await SemanticCategoryClassifier.TrainAsync(Hash, oneLabel, 5)).Should().BeNull();
    }

    [Fact]
    public async Task Classifier_generalises_to_an_unseen_spelling_variant()
    {
        var examples = new List<LabeledText>();
        string[] coffee = ["blue bottle coffee", "starbucks", "peets coffee", "dunkin donuts coffee", "coffee bean tea", "joe coffee roasters"];
        string[] fuel = ["shell oil", "chevron gas", "exxon mobil fuel", "bp gas station", "sunoco fuel", "quicktrip fuel"];
        examples.AddRange(coffee.Select(c => new LabeledText(c, "Food/Coffee")));
        examples.AddRange(fuel.Select(f => new LabeledText(f, "Transport/Fuel")));
        var model = await SemanticCategoryClassifier.TrainAsync(Hash, examples, 5);
        model.Should().NotBeNull();

        var query = (await Hash.EmbedAsync(["starbuck coffee store"]))[0];
        model!.Predict(query)!.Label.Should().Be("Food/Coffee");
        var fuelQuery = (await Hash.EmbedAsync(["shell fuel station"]))[0];
        model.Predict(fuelQuery)!.Label.Should().Be("Transport/Fuel");
    }

    [Fact]
    public async Task Classifier_gives_one_vote_per_merchant_so_a_frequent_merchant_cannot_dominate()
    {
        var examples = Enumerable.Repeat(new LabeledText("walmart", "Shopping"), 50)
            .Concat(new[] { "target store", "costco wholesale", "kroger grocery", "aldi grocery", "publix grocery", "safeway grocery", "meijer grocery", "heb grocery", "winco foods", "sprouts market" }
                .Select(m => new LabeledText(m, "Food/Groceries")))
            .ToList();
        var model = await SemanticCategoryClassifier.TrainAsync(Hash, examples, 5);
        model.Should().NotBeNull();
        model!.ExampleCount.Should().Be(11); // 50 identical rows collapsed to one example
    }

    [Fact]
    public void Ensemble_agreement_raises_confidence_and_disagreement_discounts_it()
    {
        PredictionEnsemble.Combine(new Prediction("A", 0.6), new Prediction("A", 0.5))!.Probability.Should().BeApproximately(0.8, 1e-9);
        var d = PredictionEnsemble.Combine(new Prediction("A", 0.6), new Prediction("B", 0.5))!;
        d.Label.Should().Be("A");
        d.Probability.Should().BeApproximately(0.51, 1e-9);
        PredictionEnsemble.Combine(null, new Prediction("B", 0.5))!.Label.Should().Be("B");
        PredictionEnsemble.Combine(null, null).Should().BeNull();
    }

    [Fact]
    public async Task Grouper_clusters_variants_of_one_merchant_and_leaves_others_apart()
    {
        var merchants = new[]
        {
            MerchantOccurrenceBuilder.A().WithCount(30).Build(), MerchantOccurrenceBuilder.A().WithRaw("Starbucks Store 55").Build(), MerchantOccurrenceBuilder.A()
                                                                                                                             .WithRaw("SQ *STARBUCKS")
                                                                                                                             .WithCount(3)
                                                                                                                             .Build(),
            MerchantOccurrenceBuilder.A().WithRaw("Shell Oil 9921").WithCount(20).Build(), MerchantOccurrenceBuilder.A().WithRaw("Netflix").Build(),
        };
        var groups = await MerchantGrouper.GroupAsync(Hash, merchants, 0.62);
        groups.Should().ContainSingle();
        groups[0].Canonical.Should().Be("STARBUCKS #1042"); // the most frequent spelling leads
        groups[0].Members.Select(m => m.Raw).Should().BeEquivalentTo(["STARBUCKS #1042", "Starbucks Store 55", "SQ *STARBUCKS"]);
    }

    private sealed class CountingEmbedder : ITextEmbedder
    {
        public List<string> Embedded { get; } = [];
        public string ModelId => "counting";
        public int Dimensions => 2;
        public bool IsSemantic => false;
        public double DefaultSameMerchantThreshold => 0.5;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Passage, CancellationToken ct = default)
        {
            Embedded.AddRange(texts);
            return Task.FromResult(texts.Select(t => VectorMath.Normalize([t[0], 1f])).ToArray());
        }
    }
}
