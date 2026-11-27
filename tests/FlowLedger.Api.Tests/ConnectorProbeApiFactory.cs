using System.Net;
using System.Text;
using System.Text.Json;
using FlowLedger.Application.Features.Query;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlowLedger.Api.Tests;

/// <summary>
/// Host for the tenant-isolation probes that need external collaborators (#136): Quiltt is enabled against a stub HTTP handler
/// (no network), and the natural-language query translator is a recording fake (no local model). Both stubs are deterministic.
/// </summary>
public sealed class ConnectorProbeApiFactory : FlowLedgerApiFactory
{
    public const string QuilttConnectorId = "con_test";

    /// <summary>Request bodies the stubbed Quiltt auth API received, in order.</summary>
    public List<string> QuilttSessionRequests { get; } = [];

    /// <summary>Every system prompt plus conversation the fake model was asked to complete.</summary>
    public List<string> TranslatorPrompts { get; } = [];

    private int _profiles;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // UseSetting feeds the host configuration that Program reads eagerly (ConfigureAppConfiguration can arrive too late for it).
        builder.UseSetting("Quiltt:Enabled", "true");
        builder.UseSetting("Quiltt:Environment", "sandbox");
        builder.UseSetting("Quiltt:ApiKeySecret", "quiltt-test-secret");
        builder.UseSetting("Quiltt:ConnectorId", QuilttConnectorId);
        builder.ConfigureServices(services =>
        {
            // The typed QuilttApiClient is internal; its HttpClient is registered under the type name.
            services.AddHttpClient("QuilttApiClient")
                .ConfigurePrimaryHttpMessageHandler(() => new StubQuilttHandler(this));
            services.RemoveAll<IQueryTranslator>();
            services.AddSingleton<IQueryTranslator>(new RecordingTranslator(this));
        });
    }

    private sealed class StubQuilttHandler(ConnectorProbeApiFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string profile;
            lock (owner.QuilttSessionRequests)
            {
                owner.QuilttSessionRequests.Add(body);
                var existing = JsonDocument.Parse(body.Length == 0 ? "{}" : body).RootElement.TryGetProperty("userId", out var u) ? u.GetString() : null;
                profile = existing ?? $"prof_{Interlocked.Increment(ref owner._profiles)}";
            }

            var json = JsonSerializer.Serialize(new { token = $"token-{profile}", userId = profile, expiresAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero) });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingTranslator(ConnectorProbeApiFactory owner) : IQueryTranslator
    {
        public Task<string> CompleteAsync(string system, IReadOnlyList<(string Role, string Content)> conversation, CancellationToken ct = default)
        {
            lock (owner.TranslatorPrompts)
            {
                owner.TranslatorPrompts.Add(system + "\n" + string.Join("\n", conversation.Select(t => t.Content)));
            }

            return Task.FromResult("transactions | take 5");
        }
    }
}
