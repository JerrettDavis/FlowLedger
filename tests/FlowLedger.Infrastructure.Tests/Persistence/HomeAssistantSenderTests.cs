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
            .ConfigurePrimaryHttpMessageHandler(HomeAssistantWebhookSender.CreateHandler);
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

        var error = await NewSender().SendAsync(url, new Dictionary<string, object?> { ["kind"] = "bill_due_soon", ["title"] = "Bill due soon" }, allowLocalNetwork: true);

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

        var error = await NewSender().SendAsync(url, new { kind = "x" }, allowLocalNetwork: true);

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

        var error = await NewSender().SendAsync(url, new { kind = "x" }, allowLocalNetwork: true);

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

        (await NewSender().SendAsync($"http://localhost:{port}/api/webhook/{Id}", new { kind = "x" }, allowLocalNetwork: true))
            .Should().Be("Could not reach Home Assistant.");
    }

    [Fact]
    public async Task A_non_household_tenant_cannot_reach_loopback_even_if_a_stale_url_was_stored()
    {
        var hits = 0;
        var (listener, url) = Listen(ctx => { Interlocked.Increment(ref hits); ctx.Response.Close(); return Task.CompletedTask; });
        using var _ = listener;

        (await NewSender().SendAsync(url, new { kind = "x" }, allowLocalNetwork: false)).Should().Contain("no longer valid");
        hits.Should().Be(0);
    }

    [Fact]
    public async Task The_connect_guard_refuses_a_public_looking_name_that_resolves_to_loopback()
    {
        var hits = 0;
        var (listener, url) = Listen(ctx => { Interlocked.Increment(ref hits); ctx.Response.Close(); return Task.CompletedTask; });
        using var _ = listener;
        // "localtest.me" is a public DNS name that resolves to 127.0.0.1: validation sees a public https host, only the connect guard can stop it.
        var port = new Uri(url).Port;
        var rebound = $"https://localtest.me:{port}/api/webhook/{Id}";

        (await NewSender().SendAsync(rebound, new { kind = "x" }, allowLocalNetwork: false)).Should().Be("Could not reach Home Assistant.");
        hits.Should().Be(0);
    }

    [Fact]
    public async Task An_invalid_stored_url_is_never_contacted()
        => (await NewSender().SendAsync("http://example.com/api/webhook/" + Id, new { kind = "x" }, allowLocalNetwork: true))
            .Should().Contain("no longer valid");
}
