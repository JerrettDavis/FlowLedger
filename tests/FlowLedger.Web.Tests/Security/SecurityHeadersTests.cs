using FlowLedger.Web.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace FlowLedger.Web.Tests.Security;

public sealed class SecurityHeadersTests
{
    [Fact]
    public void Csp_allows_blazor_wasm_and_the_nonced_import_map_but_no_eval_or_framing()
    {
        var csp = SecurityHeaders.BuildCsp("abc123");

        csp.Should().Contain("script-src 'self' 'wasm-unsafe-eval' 'nonce-abc123'");
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("object-src 'none'");
        csp.Should().NotContain("'unsafe-eval'");
        csp.Should().NotContain("form-action", "the OIDC sign-out POST redirects to the IdP");
        csp.Should().NotMatchRegex("script-src[^;]*'unsafe-inline'");
    }

    private sealed class ResponseFeature : HttpResponseFeature
    {
        private Func<Task>? _starting;

        public override void OnStarting(Func<object, Task> callback, object state) => _starting = () => callback(state);

        public Task FireStartingAsync() => _starting?.Invoke() ?? Task.CompletedTask;
    }

    private static async Task<HttpContext> RunAsync(bool enableCsp)
    {
        var app = new ApplicationBuilder(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()));
        app.UseFlowLedgerSecurityHeaders(enableCsp);
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();

        var feature = new ResponseFeature();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(feature);
        await pipeline(http);
        await feature.FireStartingAsync();
        return http;
    }

    [Fact]
    public async Task Middleware_sets_headers_and_a_per_request_nonce_matching_the_csp()
    {
        var http = await RunAsync(enableCsp: true);

        var nonce = SecurityHeaders.Nonce(http);
        nonce.Should().NotBeNullOrEmpty();
        http.Response.Headers.ContentSecurityPolicy.ToString().Should().Contain($"'nonce-{nonce}'");
        http.Response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        http.Response.Headers.XFrameOptions.ToString().Should().Be("DENY");
        http.Response.Headers["Referrer-Policy"].ToString().Should().Be("strict-origin-when-cross-origin");

        var other = await RunAsync(enableCsp: true);
        SecurityHeaders.Nonce(other).Should().NotBe(nonce);
    }

    [Fact]
    public async Task Development_still_forbids_framing_without_a_script_policy()
    {
        var http = await RunAsync(enableCsp: false);

        var csp = http.Response.Headers.ContentSecurityPolicy.ToString();
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().NotContain("script-src");
    }
}
