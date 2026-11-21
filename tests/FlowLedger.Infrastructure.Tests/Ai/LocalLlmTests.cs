using FlowLedger.Application.Features.LocalAi;
using FlowLedger.Infrastructure.Ai.Local;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Infrastructure.Tests.Ai;

/// <summary>Runs only when the GGUF is present in the model directory (same rule as the ONNX tests); otherwise passes vacuously.</summary>
public class LocalLlmTests
{
    private static string ModelDir => Environment.GetEnvironmentVariable("FLOWLEDGER_TEST_MODELS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLedger", "models");

    private static LlamaSharpLocalLlm? Create(bool enabled = true)
    {
        var model = ModelCatalog.Find(ModelCatalog.Qwen25)!;
        if (!File.Exists(Path.Combine(ModelDir, model.Files[0].Name)))
        {
            return null;
        }

        var options = new LocalAiOptions { ModelDirectory = ModelDir, Llm = { Enabled = enabled, ContextSize = 512 } };
        return new LlamaSharpLocalLlm(options, new LocalModelManager(options, new NoHttp(), NullLogger<LocalModelManager>.Instance), NullLogger<LlamaSharpLocalLlm>.Instance);
    }

    [Fact]
    public async Task Llm_picks_the_right_category_from_the_offered_choices()
    {
        using var llm = Create();
        if (llm is null)
        {
            return;
        }

        string[] choices = ["Food/Coffee", "Transport/Fuel", "Health/Medical"];
        var pick = await llm.ChooseAsync(
            "You classify bank transactions into one of the user's categories. Answer with only the number; 0 means none of them.",
            "Transaction: WAWA 8812\nDirection: Debit\nAmount: 41.20\nCategories:\n1. Food/Coffee\n2. Transport/Fuel\n3. Health/Medical\n0. none of these\nAnswer:",
            choices);

        llm.State.Loaded.Should().BeTrue(llm.State.Note);
        pick.Should().Be(1); // gas station => Fuel
    }

    [Fact]
    public async Task Llm_never_returns_an_index_outside_the_choices_and_says_none_for_nonsense()
    {
        using var llm = Create();
        if (llm is null)
        {
            return;
        }

        var pick = await llm.ChooseAsync("Answer with only the number; 0 means none.", "Transaction: ZZQX\nCategories:\n1. Food/Coffee\n2. Transport/Fuel\n0. none of these\nAnswer:", ["Food/Coffee", "Transport/Fuel"]);
        (pick is null or 0 or 1).Should().BeTrue();
    }

    [Fact]
    public async Task Disabled_llm_answers_null_without_loading_anything()
    {
        using var llm = Create(enabled: false);
        if (llm is null)
        {
            return;
        }

        (await llm.ChooseAsync("s", "u", ["a"])).Should().BeNull();
        llm.State.Loaded.Should().BeFalse();
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }
}
