using System.Net;
using FlowLedger.Api.Tenancy;
using FlowLedger.ServiceDefaults;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#135: Production + OIDC requires the signed tenant assertion; a missing key fails startup instead of failing open.</summary>
[Collection("ApiIntegration")]
public sealed class RequireSignedTenantTests(FailClosedApiFactory factory)
{
    private const string SigningKey = "test-signing-key-0123456789abcdef0123456789";
    private static readonly Guid Tenant = new("5f1c2a3b-0000-4000-8000-00000000abcd");

    private WebApplicationFactory<Program> Host(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });

    private static readonly (string, string)[] Oidc =
    [
        ("Auth:Oidc:Authority", "https://idp.test/"),
        ("Auth:Oidc:ClientId", "flowledger"),
        ("Api:TenantId", Tenant.ToString()),
    ];

    private static HttpClient Client(WebApplicationFactory<Program> host, string? sig)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {FlowLedgerApiFactory.DevApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Tenant.ToString());
        if (sig is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant-Sig", sig);
        }

        return client;
    }

    [Fact]
    public void Production_with_oidc_and_no_signing_key_refuses_to_start()
    {
        using var host = Host(Oidc);

        var act = () => host.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Api:TenantSigningKey*Api:RequireSignedTenant*");
    }

    [Fact]
    public void Explicit_opt_out_lets_the_legacy_unsigned_setup_start()
    {
        using var host = Host([.. Oidc, (MultiUserSettings.RequireSignedTenantKey, "false"), ("Auth:SignUp:Mode", "Closed")]);

        var act = () => host.CreateClient();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Production_with_oidc_rejects_a_valid_key_and_tenant_header_without_a_signature()
    {
        using var host = Host([.. Oidc, ("Api:TenantSigningKey", SigningKey)]);

        (await Client(host, null).GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(host, TenantAssertion.Sign("not-the-key", Tenant, factory.ServerNow)).GetAsync("/api/accounts"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(host, TenantAssertion.Sign(SigningKey, Tenant, factory.ServerNow)).GetAsync("/api/accounts"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static readonly (string, string)[] OidcWithoutTenant =
    [
        ("Auth:Oidc:Authority", "https://idp.test/"),
        ("Auth:Oidc:ClientId", "flowledger"),
        ("Api:TenantSigningKey", SigningKey),
    ];

    private async Task<string?> StoredHouseholdIdAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT value FROM system_settings WHERE key = 'household_tenant_id'", conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task Oidc_with_the_well_known_household_tenant_configured_refuses_to_start_unless_acknowledged()
    {
        using var strict = Host([.. OidcWithoutTenant, ("Api:TenantId", "00000000-0000-0000-0000-000000000001")]);
        var act = () => strict.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*well-known*Tenancy:AllowWellKnownLegacyTenantId*");

        using var acknowledged = Host([.. OidcWithoutTenant, ("Api:TenantId", "00000000-0000-0000-0000-000000000001"), (MultiUserSettings.AllowWellKnownTenantKey, "true")]);
        var ok = () => acknowledged.CreateClient();
        ok.Should().NotThrow();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_new_oidc_install_gets_a_random_household_tenant_id_generated_once_and_stored()
    {
        await factory.ResetAsync();

        Guid first;
        using (var host = Host(OidcWithoutTenant))
        {
            host.CreateClient();
            var stored = await StoredHouseholdIdAsync();
            Guid.TryParse(stored, out first).Should().BeTrue();
            first.Should().NotBe(Guid.Empty).And.NotBe(MultiUserSettings.WellKnownTenantId);
            host.Services.GetRequiredService<IConfiguration>()["Tenancy:LegacyTenantId"].Should().Be(first.ToString());
            FlowLedger.Infrastructure.TenancySettings.Legacy(host.Services.GetRequiredService<IConfiguration>()).LegacyTenantId.Should().Be(first);
        }

        using var restarted = Host(OidcWithoutTenant);
        restarted.CreateClient();
        (await StoredHouseholdIdAsync()).Should().Be(first.ToString());
        FlowLedger.Infrastructure.TenancySettings.Legacy(restarted.Services.GetRequiredService<IConfiguration>()).LegacyTenantId.Should().Be(first);
    }

    [Fact]
    public async Task A_configured_household_tenant_id_wins_and_nothing_is_generated()
    {
        await factory.ResetAsync();

        using var host = Host([.. OidcWithoutTenant, ("Api:TenantId", Tenant.ToString())]);
        host.CreateClient();

        (await StoredHouseholdIdAsync()).Should().BeNull();
        FlowLedger.Infrastructure.TenancySettings.Legacy(host.Services.GetRequiredService<IConfiguration>()).LegacyTenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task An_existing_install_with_data_and_no_configured_id_keeps_the_constant_only_when_acknowledged()
    {
        await factory.ResetAsync();
        await using (var conn = new Npgsql.NpgsqlConnection(factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"INSERT INTO tenants (id) VALUES ('{Tenant}')", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        using var strict = Host(OidcWithoutTenant);
        var act = () => strict.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*already holds data*Tenancy:AllowWellKnownLegacyTenantId*");
        (await StoredHouseholdIdAsync()).Should().BeNull("a legacy install must never get a new id: that would hide its data");

        using var acknowledged = Host([.. OidcWithoutTenant, (MultiUserSettings.AllowWellKnownTenantKey, "true")]);
        var ok = () => acknowledged.CreateClient();
        ok.Should().NotThrow();
        (await StoredHouseholdIdAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Version_skew_v1_is_accepted_by_default_and_refused_once_the_legacy_flag_is_off()
    {
        using var lenient = Host([.. Oidc, ("Api:TenantSigningKey", SigningKey)]);
        (await Client(lenient, TenantAssertion.Sign(SigningKey, Tenant, factory.ServerNow)).GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var strict = Host([.. Oidc, ("Api:TenantSigningKey", SigningKey), ("Api:AcceptLegacyTenantSignature", "false")]);
        (await Client(strict, TenantAssertion.Sign(SigningKey, Tenant, factory.ServerNow)).GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(strict, TenantAssertion.SignV2(SigningKey, Tenant, factory.ServerNow, "GET", "/api/accounts", "0123456789abcdef")).GetAsync("/api/accounts"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_v2_header_is_single_use_and_bound_to_its_endpoint()
    {
        using var host = Host([.. Oidc, ("Api:TenantSigningKey", SigningKey)]);
        var sig = TenantAssertion.SignV2(SigningKey, Tenant, factory.ServerNow, "GET", "/api/accounts", "fedcba9876543210");

        (await Client(host, sig).GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client(host, sig).GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "replayed nonce");

        var other = TenantAssertion.SignV2(SigningKey, Tenant, factory.ServerNow, "GET", "/api/accounts", "0011223344556677");
        (await Client(host, other).GetAsync("/api/categories")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "signed for another path");
    }
}
