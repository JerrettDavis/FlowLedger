using System.Net;
using System.Net.Sockets;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Infrastructure.Tests.Persistence;

public sealed class HomeAssistantSenderTests
{
    private const string Id = "AbCdEf0123456789XyZ";

    private static HomeAssistantWebhookSender NewSender()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HomeAssistantWebhookSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        return new HomeAssistantWebhookSender(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }

    private static (HttpListener Listener, string Url) Listen(Func<HttpListenerContext, Task> handler)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try { await handler(await listener.GetContextAsync()); }
                catch (Exception) { return; }
            }
        });
        return (listener, $"http://localhost:{port}/api/webhook/{Id}");
    }

    [Fact]
    public async Task Posts_the_payload_as_json_to_the_webhook()
    {
        string? body = null, method = null, path = null;
        var (listener, url) = Listen(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            body = await reader.ReadToEndAsync();
            method = ctx.Request.HttpMethod;
            path = ctx.Request.Url!.AbsolutePath;
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });
        using var _ = listener;

        var error = await NewSender().SendAsync(url, new Dictionary<string, object?> { ["kind"] = "bill_due_soon", ["title"] = "Bill due soon" });

        error.Should().BeNull();
        method.Should().Be("POST");
        path.Should().Be($"/api/webhook/{Id}");
        body.Should().Contain("\"kind\":\"bill_due_soon\"").And.NotContain("amount");
    }

    [Fact]
    public async Task A_rejecting_endpoint_yields_a_safe_summary_without_the_url()
    {
        var (listener, url) = Listen(ctx => { ctx.Response.StatusCode = 500; ctx.Response.Close(); return Task.CompletedTask; });
        using var _ = listener;

        var error = await NewSender().SendAsync(url, new { kind = "x" });

        error.Should().Be("Home Assistant answered HTTP 500.");
        error.Should().NotContain(Id);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        var hits = 0;
        var (target, targetUrl) = Listen(ctx => { Interlocked.Increment(ref hits); ctx.Response.Close(); return Task.CompletedTask; });
        using var _t = target;
        var (listener, url) = Listen(ctx =>
        {
            ctx.Response.StatusCode = 307;
            ctx.Response.RedirectLocation = targetUrl;
            ctx.Response.Close();
            return Task.CompletedTask;
        });
        using var _ = listener;

        var error = await NewSender().SendAsync(url, new { kind = "x" });

        error.Should().Be("Home Assistant answered HTTP 307.");
        hits.Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_host_is_reported_without_throwing()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        (await NewSender().SendAsync($"http://localhost:{port}/api/webhook/{Id}", new { kind = "x" }))
            .Should().Be("Could not reach Home Assistant.");
    }

    [Fact]
    public async Task An_invalid_stored_url_is_never_contacted()
        => (await NewSender().SendAsync("http://example.com/api/webhook/" + Id, new { kind = "x" }))
            .Should().Contain("no longer valid");
}
