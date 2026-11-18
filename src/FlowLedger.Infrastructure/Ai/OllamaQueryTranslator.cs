using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FlowLedger.Application.Features.Query;

namespace FlowLedger.Infrastructure.Ai;

/// <summary>Settings for the local model server (section "Ai:Ollama"). Nothing here ever points off-machine by default.</summary>
public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3:8b";
    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>Talks to a local Ollama server. Temperature 0 and thinking off so the same question yields the same query, fast.</summary>
public sealed class OllamaQueryTranslator(HttpClient http, OllamaOptions options) : IQueryTranslator
{
    public async Task<string> CompleteAsync(string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new("system", system) };
        messages.AddRange(conversation.Select(t => new ChatMessage(t.Role, t.Content)));
        var request = new ChatRequest(options.Model, messages, false, false, new ChatOptions(0));

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
        [property: JsonPropertyName("options")] ChatOptions Options);

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
}
