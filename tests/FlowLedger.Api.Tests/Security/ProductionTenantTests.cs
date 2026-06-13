using System.Net;
using FlowLedger.Application.Abstractions;
using FlowLedger.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// Tests that run the API in the Production environment (HeaderTenantContext registered).
/// Uses the shared <see cref="FailClosedApiFactory"/> from the "ApiIntegration" collection
/// so exactly one Production host exists for the whole session.
/// </summary>
[Collection("ApiIntegration")]
public sealed class ProductionTenantTests(FailClosedApiFactory factory)
{
    [Fact]
    public async Task Production_tenant_context_fails_closed_without_tenant()
    {
        var client = factory.CreateClient();

        // Authenticated with a valid API key, but NO X-Tenant-Id header → fail closed.
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {FlowLedgerApiFactory.DevApiKey}");

        var response = await client.GetAsync("/api/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Background_job_scope_resolves_household_tenant_without_http_context()
    {
        // Mirrors how OnDemandSyncJob runs: a fresh DI scope with NO HttpContext, in the
        // Production environment. The background path must NOT throw and must resolve the
        // configured household tenant — otherwise every webhook-triggered sync would
        // silently fail in Production (the original CRITICAL finding).
        using var scope = factory.Services.CreateScope();

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
}
