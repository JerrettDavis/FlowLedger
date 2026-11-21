namespace FlowLedger.Application.Features.LocalAi;

/// <summary>
/// Adjustable touchpoints for the in-process AI helpers (configuration section "LocalAi"). Every default is deliberately
/// conservative: the assistants SUGGEST, they never silently overwrite what the user chose.
/// </summary>
public sealed class LocalAiOptions
{
    public const string SectionName = "LocalAi";

    /// <summary>Master switch for the embedding-based helpers. When false the app behaves exactly as before.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>"Auto" = ONNX model when its files exist (and load), else the hashing fallback; "Hashing" forces the fallback; "Onnx" never falls back.</summary>
    public string Embedder { get; set; } = "Auto";

    /// <summary>Catalog id of the embedding model (see ModelCatalog). Swap to try bge-small, e5-small, etc.</summary>
    public string EmbeddingModel { get; set; } = "minilm-l6-v2-q8";

    /// <summary>Where model files live. Empty = a "models" folder under the per-user local application-data directory.</summary>
    public string ModelDirectory { get; set; } = string.Empty;

    /// <summary>Allow the owner to download catalog models (pinned URL + SHA-256) from the UI. Off = place the files by hand.</summary>
    public bool AllowModelDownload { get; set; } = true;

    /// <summary>Semantic predictions below this confidence are ignored. Tune from the held-out evaluation on the Local AI page.</summary>
    public double SemanticFloor { get; set; } = 0.25;

    /// <summary>How many nearest labelled merchants vote on a category.</summary>
    public int Neighbours { get; set; } = 7;

    /// <summary>Overrides the embedder's own same-merchant cosine threshold when greater than 0.</summary>
    public double SameMerchantThreshold { get; set; }

    /// <summary>Hard cap on texts embedded per request, so a huge ledger cannot pin the CPU.</summary>
    public int MaxTextsPerRequest { get; set; } = 20_000;

    /// <summary>Optional in-process generative model (GGUF) used only to break ties between the embedder's top candidates.</summary>
    public LlmOptions Llm { get; set; } = new();

    public sealed class LlmOptions
    {
        /// <summary>Opt-in. Off by default: CPU inference is slow and never runs in the sync hot path.</summary>
        public bool Enabled { get; set; }

        /// <summary>Catalog id of the GGUF model.</summary>
        public string Model { get; set; } = "qwen2.5-1.5b-instruct-q4km";

        /// <summary>Most low-confidence transactions the model may adjudicate per categorization run.</summary>
        public int MaxCallsPerRun { get; set; } = 25;

        public int ContextSize { get; set; } = 1024;

        public int Threads { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);
    }
}
