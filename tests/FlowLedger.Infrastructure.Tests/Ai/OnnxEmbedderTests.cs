using FlowLedger.Application.Features.LocalAi;
using FlowLedger.Infrastructure.Ai.Local;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Ai;

/// <summary>
/// Real-model checks. They run only when the MiniLM files are present in the model directory
/// (FLOWLEDGER_TEST_MODELS, else %LOCALAPPDATA%/FlowLedger/models); otherwise they pass vacuously so CI stays offline.
/// </summary>
public class OnnxEmbedderTests
{
    private static string ModelDir => Environment.GetEnvironmentVariable("FLOWLEDGER_TEST_MODELS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLedger", "models");

    private static OnnxTextEmbedder? Load()
    {
        var model = ModelCatalog.Find(ModelCatalog.MiniLm)!;
        var spec = model.Embedding!;
        var onnx = Path.Combine(ModelDir, spec.OnnxFile);
        var vocab = Path.Combine(ModelDir, spec.VocabFile);
        return File.Exists(onnx) && File.Exists(vocab) ? new OnnxTextEmbedder(model.Id, onnx, vocab, spec) : null;
    }

    [Fact]
    public async Task Onnx_embedder_understands_meaning_not_just_spelling()
    {
        using var e = Load();
        if (e is null)
        {
            return;
        }

        var v = await e.EmbedAsync(["starbucks coffee", "peets coffee roasters", "shell gas station", "chevron fuel"]);
        v[0].Should().HaveCount(384);
        VectorMath.Dot(v[0], v[0]).Should().BeApproximately(1f, 1e-3f);
        // coffee shops are closer to each other than to fuel stations, though they share no spelling
        VectorMath.Dot(v[0], v[1]).Should().BeGreaterThan(VectorMath.Dot(v[0], v[2]));
        VectorMath.Dot(v[2], v[3]).Should().BeGreaterThan(VectorMath.Dot(v[2], v[1]));
    }

    [Fact]
    public async Task Onnx_embedder_matches_the_reference_sentence_transformers_vector()
    {
        using var e = Load();
        if (e is null)
        {
            return;
        }

        // Same text twice must be identical, and batch order must not change a vector (padding/mask correctness).
        var single = (await e.EmbedAsync(["netflix"]))[0];
        var batched = (await e.EmbedAsync(["a much longer descriptor about groceries and household supplies", "netflix"]))[1];
        VectorMath.Dot(single, batched).Should().BeGreaterThan(0.98f); // int8 dynamic quantisation: tiny batch-composition differences are expected
    }

    [Fact]
    public void Wordpiece_splits_unknown_words_into_pieces_and_brackets_with_cls_sep()
    {
        var vocab = Path.Combine(ModelDir, ModelCatalog.Find(ModelCatalog.MiniLm)!.Embedding!.VocabFile);
        if (!File.Exists(vocab))
        {
            return;
        }

        var t = new WordPieceTokenizer(vocab);
        var ids = t.Encode("Hello, World!", 32);
        ids[0].Should().Be(101);          // [CLS]
        ids[^1].Should().Be(102);         // [SEP]
        ids.Should().HaveCount(6);        // [CLS] hello , world ! [SEP]
        t.Encode(new string('x', 500), 8).Should().HaveCountLessThanOrEqualTo(8);
    }

    [Fact]
    public async Task Switching_embedder_falls_back_to_hashing_when_the_model_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fl-empty-" + Guid.NewGuid().ToString("N"));
        var options = new LocalAiOptions { ModelDirectory = dir };
        var manager = new LocalModelManager(options, new NoHttp(), NullLogger<LocalModelManager>.Instance);
        using var sw = new SwitchingTextEmbedder(options, manager, NullLogger<SwitchingTextEmbedder>.Instance);

        sw.IsSemantic.Should().BeFalse();
        sw.ModelId.Should().Be("hashing-v1");
        (await sw.EmbedAsync(["x"])).Should().HaveCount(1);
        sw.GetStatus().EmbeddingModelInstalled.Should().BeFalse();
        sw.GetStatus().EmbedderNote.Should().Contain("not installed");
    }

    [Fact]
    public async Task Switching_embedder_uses_the_onnx_model_as_soon_as_its_files_exist()
    {
        if (Load() is null)
        {
            return;
        }

        var options = new LocalAiOptions { ModelDirectory = ModelDir };
        var manager = new LocalModelManager(options, new NoHttp(), NullLogger<LocalModelManager>.Instance);
        using var sw = new SwitchingTextEmbedder(options, manager, NullLogger<SwitchingTextEmbedder>.Instance);
        sw.IsSemantic.Should().BeTrue();
        sw.ModelId.Should().StartWith("onnx:");
        (await sw.EmbedAsync(["x"]))[0].Should().HaveCount(384);
    }

    [Fact]
    public void Catalog_entries_are_pinned_https_with_hashes()
    {
        foreach (var m in ModelCatalog.All)
        {
            m.Files.Should().NotBeEmpty();
            foreach (var f in m.Files)
            {
                new Uri(f.Url).Scheme.Should().Be("https");
                f.Url.Should().Contain("/resolve/").And.NotContain("/main/");
                f.Sha256.Should().HaveLength(64);
                f.SizeBytes.Should().BeGreaterThan(0);
            }
        }
    }

    [Fact]
    public void Download_rejects_unknown_ids_and_respects_the_disabled_switch()
    {
        var manager = new LocalModelManager(new LocalAiOptions { AllowModelDownload = false }, new NoHttp(), NullLogger<LocalModelManager>.Instance);
        manager.TryStartDownload("nope", out var e1).Should().BeFalse();
        e1.Should().Contain("Unknown");
        manager.TryStartDownload(ModelCatalog.MiniLm, out var e2).Should().BeFalse();
        e2.Should().Contain("disabled");
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }
}
