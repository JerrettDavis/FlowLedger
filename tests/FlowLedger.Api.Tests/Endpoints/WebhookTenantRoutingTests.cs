using System.Net;
using FlowLedger.Integrations.Simulated;
using FlowLedger.TestSupport;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#150: a verified webhook must sync the tenant that owns the member, never the household tenant.</summary>
[Collection("ApiIntegration")]
public sealed class WebhookTenantRoutingTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    public async Task InitializeAsync() => await factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedConnectionAsync(Guid tenant, string memberId)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO provider_connections (id, tenant_id, provider_name, member_provider_id, institution, status, created_at, updated_at, issue_retryable, issue_requires_user_action, issue_informational, consecutive_failures) " +
            "VALUES (@id, @t, 'Simulated', @m, 'Sim', 'Connected', now(), now(), false, false, false, 0)", conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("m", memberId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> AccountsAsync(Guid tenant)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM accounts WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string memberId)
    {
        using var anon = factory.CreateClient();
        var (body, sig) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(FlowLedgerApiFactory.WebhookSecret, "SYNC_COMPLETE", memberId);
        anon.DefaultRequestHeaders.Add("X-MX-Signature", sig);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await anon.PostAsync("/api/integrations/mx/webhooks", content);
    }

    [Fact]
    public async Task Webhook_syncs_the_owning_tenant_and_not_the_household_tenant()
    {
        var tenantB = Guid.NewGuid();
        await SeedConnectionAsync(tenantB, "member-of-B");

        var response = await PostWebhookAsync("member-of-B");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // The sync runs as a background job with no completion signal the host exposes: poll the observable effect.
        await Eventually.WaitAsync(async () => await AccountsAsync(tenantB) > 0, "the owning tenant's accounts appear after the webhook");

        (await AccountsAsync(tenantB)).Should().BeGreaterThan(0, "the owning tenant is the one that gets synced");
        (await AccountsAsync(FlowLedgerApiFactory.DemoTenantId)).Should().Be(0, "the household tenant must not be synced by someone else's webhook");
    }

    [Fact]
    public async Task Webhook_for_an_unknown_member_is_acknowledged_but_syncs_nobody()
    {
        var response = await PostWebhookAsync("nobody-knows-this-member");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // An unknown member never reaches the scheduler: the endpoint says so in its reply, so there is nothing to wait out.
        (await response.Content.ReadAsStringAsync()).Should().Contain("unknown member");
        (await AccountsAsync(FlowLedgerApiFactory.DemoTenantId)).Should().Be(0);
    }
}
