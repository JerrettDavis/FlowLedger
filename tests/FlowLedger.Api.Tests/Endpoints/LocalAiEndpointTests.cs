using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>ADR 0013: in-process AI helpers over HTTP (works with or without a model installed).</summary>
[Collection("ApiIntegration")]
public sealed class LocalAiEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
        await _client.PostAsync("/api/connect", null);
        await _client.PostAsync("/api/sync", null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Status_reports_the_active_embedder_and_the_catalog()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/local-ai/status"));
        var root = doc.RootElement;
        root.GetProperty("activeEmbedder").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("dimensions").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString())
            .Should().Contain(["minilm-l6-v2-q8", "qwen2.5-1.5b-instruct-q4km"]);
    }

    [Fact]
    public async Task Download_only_accepts_catalog_ids()
    {
        var response = await _client.PostAsync("/api/local-ai/models/https%3A%2F%2Fevil.example%2Fm.onnx/download", null);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Similar_for_an_unknown_transaction_is_404()
    {
        (await _client.GetAsync($"/api/local-ai/similar/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Merchant_groups_and_evaluation_answer_on_demo_data()
    {
        (await _client.GetAsync("/api/local-ai/merchant-groups")).StatusCode.Should().Be(HttpStatusCode.OK);
        var eval = await _client.PostAsync("/api/local-ai/evaluate", null);
        eval.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await eval.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("note").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Categorization_run_accepts_the_llm_flag_and_reports_zero_model_picks_when_it_is_off()
    {
        var response = await _client.PostAsync("/api/categorization/run?llm=true", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("byLlm").GetInt32().Should().Be(0);
    }
}
