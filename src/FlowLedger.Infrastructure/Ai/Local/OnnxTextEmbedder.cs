using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.LocalAi;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FlowLedger.Infrastructure.Ai.Local;

/// <summary>
/// Runs a sentence-embedding ONNX model (MiniLM / bge / e5 family) fully in-process on the CPU. Inputs are discovered from the
/// session metadata (token_type_ids is only fed when the model declares it); pooling and prefixes come from the
/// <see cref="EmbeddingSpec"/>, so swapping models is a catalog entry, not code.
/// </summary>
internal sealed class OnnxTextEmbedder : ITextEmbedder, IDisposable
{
    private const int BatchSize = 32;
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly EmbeddingSpec _spec;
    private readonly string _id;
    private readonly bool _wantsTokenTypes;
    private readonly string _output;

    public OnnxTextEmbedder(string modelId, string onnxPath, string vocabPath, EmbeddingSpec spec, int threads = 0)
    {
        _id = "onnx:" + modelId;
        _spec = spec;
        _tokenizer = new WordPieceTokenizer(vocabPath);
        using var so = new SessionOptions
        {
            IntraOpNumThreads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(onnxPath, so);
        _wantsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        _output = _session.OutputMetadata.ContainsKey("last_hidden_state") ? "last_hidden_state" : _session.OutputMetadata.Keys.First();
    }

    public string ModelId => _id;
    public int Dimensions => _spec.Dimensions;
    public bool IsSemantic => true;
    public double DefaultSameMerchantThreshold => _spec.SameMerchantThreshold;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbedKind kind = EmbedKind.Passage, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var prefix = kind == EmbedKind.Query ? _spec.QueryPrefix : _spec.PassagePrefix;
            var result = new float[texts.Count][];
            for (var offset = 0; offset < texts.Count; offset += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(BatchSize, texts.Count - offset);
                var encoded = new int[count][];
                for (var i = 0; i < count; i++)
                {
                    encoded[i] = _tokenizer.Encode(prefix + texts[offset + i], _spec.MaxTokens);
                }

                var vectors = RunBatch(encoded);
                for (var i = 0; i < count; i++)
                {
                    result[offset + i] = vectors[i];
                }
            }

            return result;
        }, ct);

    private float[][] RunBatch(int[][] encoded)
    {
        var batch = encoded.Length;
        var len = encoded.Max(e => e.Length);
        var ids = new long[batch * len];
        var mask = new long[batch * len];
        for (var b = 0; b < batch; b++)
        {
            for (var t = 0; t < len; t++)
            {
                var real = t < encoded[b].Length;
                ids[(b * len) + t] = real ? encoded[b][t] : _tokenizer.PadId;
                mask[(b * len) + t] = real ? 1 : 0;
            }
        }

        var shape = new long[] { batch, len };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [batch, len])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [batch, len])),
        };
        if (_wantsTokenTypes)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[batch * len], [batch, len])));
        }

        _ = shape;
        using var outputs = _session.Run(inputs, [_output]);
        var hidden = outputs.First().AsTensor<float>();
        var dims = hidden.Dimensions;
        var width = dims[^1];
        var flat = hidden.ToArray();
        var vectors = new float[batch][];
        for (var b = 0; b < batch; b++)
        {
            var v = new float[width];
            if (string.Equals(_spec.Pooling, "cls", StringComparison.OrdinalIgnoreCase))
            {
                Array.Copy(flat, b * len * width, v, 0, width);
            }
            else
            {
                var n = 0;
                for (var t = 0; t < encoded[b].Length; t++)
                {
                    var o = ((b * len) + t) * width;
                    for (var d = 0; d < width; d++)
                    {
                        v[d] += flat[o + d];
                    }

                    n++;
                }

                for (var d = 0; d < width; d++)
                {
                    v[d] /= Math.Max(1, n);
                }
            }

            vectors[b] = VectorMath.Normalize(v);
        }

        return vectors;
    }

    public void Dispose() => _session.Dispose();
}
