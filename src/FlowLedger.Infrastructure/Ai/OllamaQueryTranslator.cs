using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Features.Query;

namespace FlowLedger.Infrastructure.Ai;

/// <summary>Settings for the local model server (section "Ai:Ollama"). Nothing here ever points off-machine by default.</summary>
internal sealed class OllamaOptions
{
    public const string SectionName = "Ai:Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3:8b";
    /// <summary>Generous: the first question after the model was unloaded must wait for it to load into memory.</summary>
    public int TimeoutSeconds { get; set; } = 240;

    /// <summary>How long Ollama keeps the model loaded after a request.</summary>
    public string KeepAlive { get; set; } = "30m";
}

/// <summary>Talks to a local Ollama server. Temperature 0 and thinking off so the same question yields the same query, fast.</summary>
internal sealed class OllamaQueryTranslator(HttpClient http, OllamaOptions options) : IQueryTranslator
{
    public async Task<string> CompleteAsync(string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new("system", system) };
        messages.AddRange(conversation.Select(t => new ChatMessage(t.Role, t.Content)));
        var request = new ChatRequest(options.Model, messages, false, false, options.KeepAlive, new ChatOptions(0));

        using var response = await http.PostAsJsonAsync("api/chat", request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty response from the local model.");
        return body.Message?.Content ?? string.Empty;
    }

    private sealed record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

    private sealed record ChatOptions([property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("think")] bool Think,
        [property: JsonPropertyName("keep_alive")] string KeepAlive,
        [property: JsonPropertyName("options")] ChatOptions Options);

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
}
