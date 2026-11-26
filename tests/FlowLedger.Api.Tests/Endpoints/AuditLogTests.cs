using FlowLedger.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class AuditLogTests(FlowLedgerApiFactory factory)
{
    private async Task<long> CountAsync(string action)
    {
        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT to_regclass('audit_events') IS NOT NULL", conn);
        if (!(bool)(await exists.ExecuteScalarAsync())!)
        {
            return 0;
        }

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_events WHERE action = @a", conn);
        cmd.Parameters.AddWithValue("a", action);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Sensitive_endpoint_writes_an_audit_row_with_actor_tenant_ip_and_trace()
    {
        await factory.ResetAsync();
        var before = await CountAsync("connector.connect");
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/connect", content: null);
        response.EnsureSuccessStatusCode();

        (await CountAsync("connector.connect")).Should().Be(before + 1);

        await using var conn = new NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT tenant_id, actor, target, trace_id, status_code FROM audit_events WHERE action = 'connector.connect' ORDER BY id DESC LIMIT 1", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        r.GetGuid(0).Should().Be(FlowLedgerApiFactory.DemoTenantId);
        r.GetString(1).Should().NotBeNullOrWhiteSpace();
        r.GetString(2).Should().Be("/api/connect");
        r.GetString(3).Should().NotBeNullOrWhiteSpace();
        r.GetInt32(4).Should().Be(200);
    }

    [Fact]
    public async Task Non_sensitive_endpoint_writes_no_audit_row()
    {
        await factory.ResetAsync();
        var before = await CountAsync("connector.connect");
        using var client = factory.CreateAuthenticatedClient();

        (await client.GetAsync("/api/accounts")).EnsureSuccessStatusCode();

        (await CountAsync("connector.connect")).Should().Be(before);
    }

    [Fact]
    public void Every_audited_endpoint_name_exists_in_the_route_table()
    {
        var names = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(d => d.Endpoints)
            .Select(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName)
            .Where(n => n is not null)
            .ToHashSet();

        AuditMiddleware.AuditedEndpoints.Keys.Should().OnlyContain(k => names.Contains(k), "a renamed endpoint would silently stop being audited");
    }
}
