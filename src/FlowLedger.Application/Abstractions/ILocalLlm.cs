namespace FlowLedger.Application.Abstractions;

/// <summary>Whether the optional in-process generative model can answer right now.</summary>
public sealed record LocalLlmState(bool Enabled, bool ModelInstalled, bool Loaded, string? Note);

/// <summary>
/// A small in-process chat model (llama.cpp / GGUF) used ONLY to choose one of a few offered options. The answer is
/// grammar-constrained to a choice number, so it can never produce free text or an unlisted category.
/// </summary>
public interface ILocalLlm
{
    LocalLlmState State { get; }

    /// <summary>Index into <paramref name="choices"/> of the option the model picks; null for "none of these" or when unavailable.</summary>
    Task<int?> ChooseAsync(string system, string user, IReadOnlyList<string> choices, CancellationToken ct = default);
}
