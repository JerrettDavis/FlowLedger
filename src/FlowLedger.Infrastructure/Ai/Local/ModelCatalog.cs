namespace FlowLedger.Infrastructure.Ai.Local;

/// <summary>One downloadable file, pinned to an exact Hugging Face revision and verified by SHA-256 after download.</summary>
public sealed record ModelFile(string Name, string Url, string Sha256, long SizeBytes);

/// <summary>How to run a sentence-embedding ONNX model (pooling, prefixes, calibration). Every field is a swap-in touchpoint.</summary>
public sealed record EmbeddingSpec(
    int Dimensions,
    int MaxTokens,
    string Pooling,
    string PassagePrefix,
    string QueryPrefix,
    string OnnxFile,
    string VocabFile,
    double SameMerchantThreshold);

/// <summary>How to prompt a GGUF chat model. "chatml" covers Qwen; add templates for Llama/Gemma as needed.</summary>
public sealed record LlmSpec(string ChatTemplate);

public sealed record CatalogModel(
    string Id,
    string Kind,
    string Title,
    string Description,
    string License,
    IReadOnlyList<ModelFile> Files,
    EmbeddingSpec? Embedding = null,
    LlmSpec? Llm = null)
{
    public long SizeBytes => Files.Sum(f => f.SizeBytes);
}

/// <summary>
/// The models this build knows how to run. URLs are pinned to a commit and hashes are checked, and a request can only ever
/// name a catalog id (never a URL), so the download endpoint cannot be pointed at an arbitrary server.
/// To plug in another model: add an entry here (or drop the files into the model directory with the same names).
/// </summary>
public static class ModelCatalog
{
    public const string MiniLm = "minilm-l6-v2-q8";
    public const string BgeSmall = "bge-small-en-v1.5-q8";

    public const string Qwen25 = "qwen2.5-1.5b-instruct-q4km";

    private const string QwenRev = "91cad51170dc346986eccefdc2dd33a9da36ead9";
    private const string MiniLmRev = "751bff37182d3f1213fa05d7196b954e230abad9";
    private const string BgeRev = "ea104dacec62c0de699686887e3f920caeb4f3e3";

    public static IReadOnlyList<CatalogModel> All { get; } =
    [
        new(MiniLm, "embedding", "all-MiniLM-L6-v2 (int8)",
            "Default. 22M-parameter sentence embedder, 384-d, ~23 MB: fastest option, strong on short product/merchant text, ~ms per descriptor on CPU.",
            "Apache-2.0",
            [
                new("minilm-l6-v2-q8.onnx", $"https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/{MiniLmRev}/onnx/model_quantized.onnx",
                    "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1", 22_972_370),
                new("minilm-l6-v2.vocab.txt", $"https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/{MiniLmRev}/vocab.txt",
                    "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3", 231_508),
            ],
            new EmbeddingSpec(384, 64, "mean", string.Empty, string.Empty, "minilm-l6-v2-q8.onnx", "minilm-l6-v2.vocab.txt", 0.80)),

        new(BgeSmall, "embedding", "bge-small-en-v1.5 (int8)",
            "Higher-accuracy alternative. 33M-parameter, 384-d, ~34 MB: better retrieval quality on MTEB at roughly 1.5x the CPU cost of MiniLM.",
            "MIT",
            [
                new("bge-small-en-v1.5-q8.onnx", $"https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/{BgeRev}/onnx/model_quantized.onnx",
                    "6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4", 34_014_426),
                new("bge-small-en-v1.5.vocab.txt", $"https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/{BgeRev}/vocab.txt",
                    "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3", 231_508),
            ],
            new EmbeddingSpec(384, 64, "cls", string.Empty, string.Empty, "bge-small-en-v1.5-q8.onnx", "bge-small-en-v1.5.vocab.txt", 0.86)),

        new(Qwen25, "llm", "Qwen2.5-1.5B-Instruct (Q4_K_M, GGUF)",
            "Optional tie-breaker for transactions the embedder is unsure about. ~1.1 GB, runs on CPU via llama.cpp inside FlowLedger; only ever picks from the embedder's candidates or \"none\".",
            "Apache-2.0",
            [
                new("qwen2.5-1.5b-instruct-q4_k_m.gguf", $"https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/{QwenRev}/qwen2.5-1.5b-instruct-q4_k_m.gguf",
                    "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e", 1_117_320_736),
            ],
            Llm: new LlmSpec("chatml")),
    ];

    public static CatalogModel? Find(string? id) => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
