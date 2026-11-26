using System.Net;
using System.Text;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Web.Tests.Services;

public sealed class LayoutStatusLoaderTests
{
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _bothStarted = new();
        private int _started;
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            // Only completes when both requests are in flight: proves they were issued concurrently.
            if (Interlocked.Increment(ref _started) == 2)
            {
                _bothStarted.SetResult();
            }

            await _bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var json = request.RequestUri.AbsolutePath == "/api/environment"
                ? """{"environment":"production","isProduction":true}"""
                : """{"environment":"production","backup":{"healthy":true,"stale":false,"offsiteConfigured":false,"lastSuccessUtc":null,"lastRun":null,"runs":[]},"providerUsage":{"provider":"x","callsToday":0,"callsThisMonth":0,"dailyBudget":1,"monthlyBudget":1,"paused":false,"byEndpoint":[]}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static FlowLedgerApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance);

    [Fact]
    public async Task Loads_environment_and_data_status_concurrently_with_one_call_each()
    {
        var handler = new GatedHandler();

        var status = await LayoutStatusLoader.LoadAsync(Client(handler));

        handler.Paths.Should().BeEquivalentTo("/api/environment", "/api/data/status");
        status.Environment.Should().Be("production");
        status.BackupAlert.Should().Be("No off-site backup configured");
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("down");
    }

    [Fact]
    public async Task Api_failure_leaves_environment_unknown_instead_of_throwing()
    {
        var status = await LayoutStatusLoader.LoadAsync(Client(new FailingHandler()));

        status.Environment.Should().Be("unknown");
        status.BackupAlert.Should().BeNull();
        status.IsKnown.Should().BeFalse();
    }
}
