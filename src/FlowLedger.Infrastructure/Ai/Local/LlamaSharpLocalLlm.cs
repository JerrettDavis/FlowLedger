using System.Globalization;
using System.Text;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.LocalAi;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Ai.Local;

/// <summary>
/// llama.cpp (via LLamaSharp) running a small GGUF chat model inside this process. Loaded lazily on first use, one inference at a
/// time (a CPU-bound model gains nothing from concurrency), answer constrained by a GBNF grammar to a choice number.
/// If the native backend or the model cannot load, <see cref="State"/> says so and <see cref="ChooseAsync"/> returns null: the
/// caller then simply keeps the embedder's answer. Never started by the sync pipeline, only by an explicit categorization run.
/// </summary>
internal sealed partial class LlamaSharpLocalLlm : ILocalLlm, IDisposable
{
    private readonly LocalAiOptions _options;
    private readonly LocalModelManager _models;
    private readonly ILogger<LlamaSharpLocalLlm> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private ModelParams? _params;
    private string? _loadedId;
    private string? _failure;
    private DateTime _failedAtUtc;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    public LlamaSharpLocalLlm(LocalAiOptions options, LocalModelManager models, ILogger<LlamaSharpLocalLlm> logger)
    {
        _options = options;
        _models = models;
        _logger = logger;
    }

    public LocalLlmState State
    {
        get
        {
            var model = ModelCatalog.Find(_options.Llm.Model);
            var installed = model?.Llm is not null && _models.IsInstalled(model);
            var note = !_options.Enabled || !_options.Llm.Enabled ? "Disabled (set LocalAi:Llm:Enabled=true)."
                : model?.Llm is null ? $"Unknown LLM '{_options.Llm.Model}'."
                : !installed ? "Model not installed."
                : _failure;
            return new LocalLlmState(_options.Enabled && _options.Llm.Enabled, installed, _weights is not null, note);
        }
    }

    public async Task<int?> ChooseAsync(string system, string user, IReadOnlyList<string> choices, CancellationToken ct = default)
    {
        var state = State;
        if (!state.Enabled || !state.ModelInstalled || choices.Count == 0 || (_failure is not null && _weights is null && DateTime.UtcNow - _failedAtUtc < RetryAfter))
        {
            return null;
        }

        var model = ModelCatalog.Find(_options.Llm.Model)!;
        await _gate.WaitAsync(ct);
        try
        {
            if (!EnsureLoaded(model))
            {
                return null;
            }

            var prompt = BuildPrompt(model.Llm!.ChatTemplate, system, user);
            // 1..N pick an option, 0 = none of them.
            var grammar = "root ::= (" + string.Join(" | ", Enumerable.Range(0, choices.Count + 1).Select(i => $"\"{i}\"")) + ")";
            var executor = new StatelessExecutor(_weights!, _params!);
            var inference = new InferenceParams
            {
                MaxTokens = 3,
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f, Grammar = new Grammar(grammar, "root") },
            };

            var sb = new StringBuilder();
            await foreach (var piece in executor.InferAsync(prompt, inference, ct))
            {
                sb.Append(piece);
            }

            return int.TryParse(sb.ToString().Trim(), CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= choices.Count ? n - 1 : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failedAtUtc = DateTime.UtcNow;
            _failure = "Inference failed (" + ex.GetType().Name + "): " + ex.Message;
            LogLocalLLMInferenceFailed(_logger, ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool EnsureLoaded(CatalogModel model)
    {
        if (_weights is not null && _loadedId == model.Id)
        {
            return true;
        }

        try
        {
            _weights?.Dispose();
            _weights = null;
            if (!_models.VerifyIntegrity(model))
            {
                throw new InvalidDataException("A model file does not match its catalog checksum; remove the model and download it again.");
            }

            var path = Path.Combine(_models.Directory, model.Files[0].Name);
            _params = new ModelParams(path)
            {
                ContextSize = (uint)Math.Clamp(_options.Llm.ContextSize, 256, 8192),
                GpuLayerCount = 0,
                Threads = Math.Max(1, _options.Llm.Threads),
            };
            _weights = LLamaWeights.LoadFromFile(_params);
            _loadedId = model.Id;
            _failure = null;
            LogLocalLLMLoaded(_logger, model.Id);
            return true;
        }
        catch (Exception ex)
        {
            _failedAtUtc = DateTime.UtcNow;
            _failure = "Could not load the model (" + ex.GetType().Name + "): " + ex.Message;
            LogLocalLLMFailedToLoad(_logger, ex, model.Id);
            return false;
        }
    }

    private static string BuildPrompt(string template, string system, string user) => template switch
    {
        // ChatML (Qwen family)
        _ => $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n",
    };

    public void Dispose()
    {
        _weights?.Dispose();
        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local LLM inference failed")]
    private static partial void LogLocalLLMInferenceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Local LLM loaded: {Model}")]
    private static partial void LogLocalLLMLoaded(ILogger logger, string? model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local LLM {Model} failed to load")]
    private static partial void LogLocalLLMFailedToLoad(ILogger logger, Exception exception, string? model);
}
