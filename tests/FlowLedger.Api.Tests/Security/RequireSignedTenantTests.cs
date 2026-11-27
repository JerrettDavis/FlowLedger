using System.Net;
using FlowLedger.Api.Tenancy;
using FlowLedger.ServiceDefaults;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

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

    [Fact]
    public void Oidc_with_the_well_known_household_tenant_refuses_to_start_unless_acknowledged()
    {
        var withoutTenant = new[] { ("Auth:Oidc:Authority", "https://idp.test/"), ("Auth:Oidc:ClientId", "flowledger"), ("Api:TenantSigningKey", SigningKey) };

        using var strict = Host(withoutTenant);
        var act = () => strict.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*well-known*Tenancy:AllowWellKnownLegacyTenantId*");

        using var acknowledged = Host([.. withoutTenant, (MultiUserSettings.AllowWellKnownTenantKey, "true")]);
        var ok = () => acknowledged.CreateClient();
        ok.Should().NotThrow();
    }
}
