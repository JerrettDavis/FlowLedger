using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.LocalAi;

/// <summary>
/// Dependency-free lexical embedder (feature hashing of words and character 3-5-grams into a signed 512-d vector).
/// It knows spelling, not meaning: "starbucks" ~ "starbuck coffee" but never "starbucks" ~ "peets". It exists so every
/// feature works with zero downloads and so tests are deterministic; the ONNX model replaces it when installed.
/// </summary>
public sealed class HashingTextEmbedder : ITextEmbedder
{
    public const int Size = 512;

    public string ModelId => "hashing-v1";
    public int Dimensions => Size;
    public bool IsSemantic => false;
    public double DefaultSameMerchantThreshold => 0.62;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Passage, CancellationToken ct = default)
        => Task.FromResult(texts.Select(Embed).ToArray());

    public static float[] Embed(string text)
    {
        var v = new float[Size];
        var norm = DescriptorNormalizer.Normalize(null, text);
        foreach (var word in norm.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Add(v, "w:" + word, 1.5f);
            var padded = "^" + word + "$";
            for (var n = 3; n <= 5; n++)
            {
                for (var i = 0; i + n <= padded.Length; i++)
                {
                    Add(v, padded.Substring(i, n), 1f);
                }
            }
        }

        return VectorMath.Normalize(v);
    }

    private static void Add(float[] v, string feature, float weight)
    {
        // FNV-1a: stable across processes (string.GetHashCode is randomised per run).
        uint h = 2166136261;
        foreach (var ch in feature)
        {
            h = (h ^ ch) * 16777619;
        }

        var index = (int)(h % Size);
        v[index] += ((h >> 31) & 1) == 0 ? weight : -weight;
    }
}
