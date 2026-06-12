using System.Net;
using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Integration tests for the Phase 5 security pipeline: API-key authentication,
/// fail-closed tenant resolution, anonymous health/webhook endpoints, secure headers,
/// and rate limiting. Runs against a real Postgres Testcontainer.
/// </summary>
[Collection("ApiIntegration")]
public sealed class ApiSecurityTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Unauthenticated_request_to_api_returns_401()
    {
        var response = await _client.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_with_wrong_api_key_returns_401()
    {
        _client.DefaultRequestHeaders.Add("Authorization", "Bearer wrong-key-value");
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", FlowLedgerApiFactory.DemoTenantId.ToString());

        var response = await _client.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_request_with_valid_api_key_succeeds()
    {
        var authenticated = factory.CreateAuthenticatedClient();

        var response = await authenticated.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_and_liveness_endpoints_are_anonymous()
    {
        var health = await _client.GetAsync("/health");
        var alive = await _client.GetAsync("/alive");

        health.StatusCode.Should().Be(HttpStatusCode.OK);
        alive.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_with_bad_signature_returns_401_and_is_reachable_anonymously()
    {
        // No API key supplied. If the auth layer blocked it, we'd get a 401 carrying a
        // WWW-Authenticate challenge header. Instead, the request must reach the handler
        // and be rejected by the HMAC check — also a 401, but WITHOUT WWW-Authenticate.
        _client.DefaultRequestHeaders.Add("X-MX-Signature", "invalid-hmac-value");
        var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/integrations/mx/webhooks", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("WWW-Authenticate").Should().BeFalse(
            "the auth layer must pass the anonymous webhook through; the 401 comes from the HMAC check");
    }

    [Fact]
    public async Task Security_headers_are_present_on_responses()
    {
        var authenticated = factory.CreateAuthenticatedClient();

        var response = await authenticated.GetAsync("/api/accounts");

        response.Headers.Should().ContainKey("X-Content-Type-Options");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.Should().ContainKey("X-Frame-Options");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.Should().ContainKey("Referrer-Policy");
        response.Headers.Should().ContainKey("X-XSS-Protection");
    }

    [Fact]
    public async Task Production_tenant_context_fails_closed_without_tenant()
    {
        await using var prodFactory = new FailClosedApiFactory();
        await prodFactory.InitializeAsync();
        var client = prodFactory.CreateClient();

        // Authenticated with a valid API key, but NO X-Tenant-Id header.
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {FlowLedgerApiFactory.DevApiKey}");

        var response = await client.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Background_job_scope_resolves_household_tenant_without_http_context()
    {
        // Mirrors how OnDemandSyncJob runs: a fresh DI scope with NO HttpContext, in the
        // Production environment (HeaderTenantContext registered). The background path must
        // NOT throw and must resolve the configured household tenant — otherwise every
        // webhook-triggered sync would silently fail in Production.
        await using var prodFactory = new FailClosedApiFactory();
        await prodFactory.InitializeAsync();

        // Touch Services to ensure the host is started, then create a job-style scope.
        await using var scope = prodFactory.Services.CreateAsyncScope();

        // No HttpContext exists in this scope (no request).
        var resolve = () => scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var tenantContext = resolve.Should().NotThrow().Subject;

        tenantContext.TenantId.Should().Be(FlowLedgerApiFactory.DemoTenantId,
            "the background path falls back to the configured household tenant (Api:TenantId)");
        tenantContext.UserId.Should().Be(FlowLedgerApiFactory.DemoTenantId);

        // The sync service (which depends transitively on ITenantContext via the DbContext
        // and sync cursor store) must also resolve without throwing in this scope.
        var resolveSync = () => scope.ServiceProvider.GetRequiredService<IFinancialSyncService>();
        resolveSync.Should().NotThrow();
    }

    [Fact]
    public async Task Webhook_rate_limit_engages_after_threshold()
    {
        // The "webhook" policy permits 30 requests/min with no queue. Each request here
        // fails the HMAC check (401) but still consumes a permit, so firing well past the
        // limit within the same window deterministically produces at least one 429.
        // (The webhook limiter is a distinct partition from the "api"/"write" policies,
        //  so other tests cannot prevent this from triggering.)
        _client.DefaultRequestHeaders.Add("X-MX-Signature", "invalid-hmac-value");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 45; i++)
        {
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("/api/integrations/mx/webhooks", content);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "requests beyond the webhook permit limit (30/min) must be rejected with 429");
    }
}
