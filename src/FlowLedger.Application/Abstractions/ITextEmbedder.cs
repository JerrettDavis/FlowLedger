namespace FlowLedger.Application.Abstractions;

/// <summary>What the text is for. Asymmetric models (e5, bge) want a different prefix for a search query than for a stored passage.</summary>
public enum EmbedKind { Passage, Query }

/// <summary>
/// Turns short texts (bank descriptors, merchant names, category names) into fixed-length unit vectors so "how alike are these
/// two?" becomes a dot product. Implementations run IN-PROCESS (no server): an ONNX sentence-embedding model when its files are
/// present, otherwise a dependency-free hashing embedder. Vectors are L2-normalised, so cosine similarity == dot product.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>Identifies the model so cached vectors are never mixed across models (e.g. "onnx:minilm-l6-v2-q8", "hashing-v1").</summary>
    string ModelId { get; }

    int Dimensions { get; }

    /// <summary>True for a learned semantic model; false for the lexical fallback (which only knows spelling, not meaning).</summary>
    bool IsSemantic { get; }

    /// <summary>Cosine similarity at/above which two merchant names are likely the same merchant, calibrated per model.</summary>
    double DefaultSameMerchantThreshold { get; }

    /// <summary>Embeds a batch. The result is parallel to <paramref name="texts"/>; each vector has <see cref="Dimensions"/> floats.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Passage, CancellationToken ct = default);
}
