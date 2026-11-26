using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

/// <summary>
/// /api/data/* acts on (or reports on) the WHOLE database, across every tenant. Only the household (legacy) tenant
/// may use it; any other tenant (e.g. a second OIDC user) must be refused before any work is done.
/// </summary>
[Collection("ApiIntegration")]
public sealed class DataAdminTenantTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("GET", "/api/data/status")]
    [InlineData("GET", "/api/data/export")]
    [InlineData("POST", "/api/data/backup-now")]
    [InlineData("POST", "/api/data/import")]
    public async Task Another_tenant_is_forbidden(string method, string path)
    {
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (path.EndsWith("/import", StringComparison.Ordinal))
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent([1, 2, 3]);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "file", "export.zip");
            req.Content = form;
        }

        var resp = await other.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resolve_reports_a_new_user_as_not_household()
    {
        var client = factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/users/resolve",
            new { issuer = "https://idp.test/" + Guid.NewGuid(), subject = "someone", email = (string?)null, displayName = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<ResolveBody>();
        body!.IsHousehold.Should().BeFalse();
        body.TenantId.Should().NotBe(FlowLedgerApiFactory.DemoTenantId);
    }

    private sealed record ResolveBody(Guid TenantId, bool IsHousehold);

    [Fact]
    public async Task Household_tenant_may_read_status()
    {
        var household = factory.CreateAuthenticatedClient(FlowLedgerApiFactory.DemoTenantId);

        var resp = await household.GetAsync("/api/data/status");

        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_scope_is_validated_and_a_tenant_scoped_export_is_a_zip_for_that_tenant_only()
    {
        var household = factory.CreateAuthenticatedClient(FlowLedgerApiFactory.DemoTenantId);

        (await household.GetAsync("/api/data/export?scope=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var scoped = await household.GetAsync("/api/data/export?scope=tenant");
        scoped.StatusCode.Should().Be(HttpStatusCode.OK);
        scoped.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        scoped.Content.Headers.ContentDisposition!.FileName.Should().Contain("tenant");

        // A non-household tenant cannot reach the export, scoped or not.
        var other = factory.CreateAuthenticatedClient(Guid.NewGuid());
        (await other.GetAsync($"/api/data/export?tenantId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
