using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.LocalAi;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Ai.Local;

/// <summary>
/// The app's single <see cref="ITextEmbedder"/>. Picks the learned ONNX model when its files exist and load, otherwise the
/// hashing fallback, re-checking on every call so a model installed from the UI is used immediately without a restart.
/// Each backend has its own vector cache, so vectors from different models can never be mixed.
/// Native-load or model errors degrade to the fallback and surface in <see cref="GetStatus"/>; they never throw into callers
/// (except in mode "Onnx", which is explicit about never falling back).
/// </summary>
internal sealed class SwitchingTextEmbedder : ITextEmbedder, ILocalAiRuntime, IDisposable
{
    private readonly LocalAiOptions _options;
    private readonly LocalModelManager _models;
    private readonly ILogger<SwitchingTextEmbedder> _logger;
    private readonly CachingTextEmbedder _hashing = new(new HashingTextEmbedder());
    private readonly object _gate = new();
    private CachingTextEmbedder? _onnx;
    private OnnxTextEmbedder? _onnxRaw;
    private string? _loadedId;
    private string? _note;
    private string? _failedId;
    private readonly ILocalLlm? _llm;

    public SwitchingTextEmbedder(LocalAiOptions options, LocalModelManager models, ILogger<SwitchingTextEmbedder> logger, ILocalLlm? llm = null)
    {
        _llm = llm;
        _options = options;
        _models = models;
        _logger = logger;
    }

    private ITextEmbedder Active => ResolveOnnx() ?? _hashing;

    public string ModelId => Active.ModelId;
    public int Dimensions => Active.Dimensions;
    public bool IsSemantic => Active.IsSemantic;

    public double DefaultSameMerchantThreshold => Active.DefaultSameMerchantThreshold;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Passage, CancellationToken ct = default)
        => Active.EmbedAsync(texts, kind, ct);

    private CachingTextEmbedder? ResolveOnnx()
    {
        if (!_options.Enabled || string.Equals(_options.Embedder, "Hashing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var model = ModelCatalog.Find(_options.EmbeddingModel);
        if (model?.Embedding is null)
        {
            _note = $"Unknown embedding model '{_options.EmbeddingModel}'.";
            return Strict(_note);
        }

        if (!_models.IsInstalled(model))
        {
            _note = "Embedding model not installed; using the lexical fallback.";
            return Strict("Embedding model not installed.");
        }

        lock (_gate)
        {
            if (_loadedId == model.Id && _onnx is not null)
            {
                return _onnx;
            }

            if (_failedId == model.Id)
            {
                return Strict(_note ?? "Embedding model failed to load.");
            }

            try
            {
                if (!_models.VerifyIntegrity(model))
                {
                    throw new InvalidDataException("A model file does not match its catalog checksum; remove the model and download it again.");
                }

                var spec = model.Embedding;
                var raw = new OnnxTextEmbedder(model.Id, Path.Combine(_models.Directory, spec.OnnxFile), Path.Combine(_models.Directory, spec.VocabFile), spec);
                _onnxRaw?.Dispose();
                _onnxRaw = raw;
                _onnx = new CachingTextEmbedder(raw);
                _loadedId = model.Id;
                _failedId = null;
                _note = null;
                _logger.LogInformation("Local AI embedder active: {Model}", raw.ModelId);
                return _onnx;
            }
            catch (Exception ex)
            {
                _failedId = model.Id;
                _note = "Embedding model could not be loaded (" + ex.GetType().Name + ": " + ex.Message + "); using the lexical fallback.";
                _logger.LogWarning(ex, "Local AI embedding model {Model} failed to load", model.Id);
                return Strict(_note);
            }
        }
    }

    private CachingTextEmbedder? Strict(string reason)
        => string.Equals(_options.Embedder, "Onnx", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("LocalAi:Embedder=Onnx but the model is unavailable: " + reason)
            : null;

    public LocalAiRuntimeStatus GetStatus()
    {
        ITextEmbedder active;
        try
        {
            active = Active;
        }
        catch (InvalidOperationException)
        {
            active = _hashing;
        }

        var selected = ModelCatalog.Find(_options.EmbeddingModel);
        var llmSelected = ModelCatalog.Find(_options.Llm.Model);
        var models = ModelCatalog.All
            .Select(m => new LocalAiModelInfo(
                m.Id, m.Kind, m.Title, m.Description, m.SizeBytes, _models.IsInstalled(m),
                string.Equals(m.Id, _options.EmbeddingModel, StringComparison.OrdinalIgnoreCase)
                || (m.Kind == "llm" && string.Equals(m.Id, _options.Llm.Model, StringComparison.OrdinalIgnoreCase)),
                m.License))
            .ToList();
        return new LocalAiRuntimeStatus(
            _options.Enabled ? _options.Embedder : "Disabled",
            active.ModelId,
            active.Dimensions,
            active.IsSemantic,
            selected is not null && _models.IsInstalled(selected),
            _note,
            _options.EmbeddingModel,
            _llm?.State.Enabled ?? _options.Llm.Enabled,
            llmSelected is not null && _models.IsInstalled(llmSelected),
            _llm?.State.Loaded ?? false,
            _llm?.State.Note,
            _models.Directory,
            _options.AllowModelDownload,
            models,
            _models.Download);
    }

    public void Dispose() => _onnxRaw?.Dispose();
}
