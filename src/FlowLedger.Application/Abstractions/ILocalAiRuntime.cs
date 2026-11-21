namespace FlowLedger.Application.Abstractions;

/// <summary>Which in-process AI capabilities are really active right now (as opposed to merely configured).</summary>
public sealed record LocalAiRuntimeStatus(
    string Mode,
    string ActiveEmbedder,
    int Dimensions,
    bool Semantic,
    bool EmbeddingModelInstalled,
    string? EmbedderNote,
    string EmbeddingModelId,
    bool LlmEnabled,
    bool LlmModelInstalled,
    bool LlmLoaded,
    string? LlmNote,
    string ModelDirectory,
    bool DownloadsAllowed,
    IReadOnlyList<LocalAiModelInfo> Models,
    LocalAiDownload? Download = null);

/// <summary>One catalog model: what it is for, how big, and whether its files are on disk.</summary>
public sealed record LocalAiModelInfo(
    string Id, string Kind, string Title, string Description, long SizeBytes, bool Installed, bool Selected, string License);

/// <summary>Implemented by the infrastructure embedder so the API can report truthfully which backend is live.</summary>
public interface ILocalAiRuntime
{
    LocalAiRuntimeStatus GetStatus();
}

/// <summary>Progress of the (single) model download in flight or the last one that finished.</summary>
public sealed record LocalAiDownload(string ModelId, string State, long ReceivedBytes, long TotalBytes, string? Error);

/// <summary>Owner-initiated, catalog-only model installation. Never takes a URL.</summary>
public interface ILocalAiModels
{
    /// <summary>Starts a background download of a catalog model; false (with a reason) when unknown, disabled or already running.</summary>
    bool TryStartDownload(string modelId, out string? error);

    /// <summary>Deletes an installed model's files so disk can be reclaimed.</summary>
    bool Remove(string modelId);
}
