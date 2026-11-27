using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowLedger.Api.Auth;
using FlowLedger.ServiceDefaults;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlowLedger.Api.Tests.Security;

/// <summary>#145: sign-up modes at the API (<c>/api/users/resolve</c>) and the billable-provisioning gate.</summary>
[Collection("ApiIntegration")]
public sealed class SignUpPolicyTests(FlowLedgerApiFactory factory)
{
    private HttpClient Client(params (string Key, string Value)[] settings)
    {
        var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Oidc:Authority", "https://idp.test/");
            b.UseSetting("Auth:Oidc:ClientId", "flowledger");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {FlowLedgerApiFactory.DevApiKey}");
        return client;
    }

    private static Task<HttpResponseMessage> Resolve(HttpClient client, string issuer, string subject, string? email = null, bool allowProvisioning = true) =>
        client.PostAsJsonAsync("/api/users/resolve",
            new { issuer, subject, email, displayName = subject, emailVerified = email is not null, allowProvisioning });

    private static string NewIssuer() => "https://idp.test/" + Guid.NewGuid();

    [Fact]
    public async Task Default_with_oidc_on_and_no_allowlist_refuses_new_logins()
    {
        var client = Client();

        var resp = await Resolve(client, NewIssuer(), "stranger");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "InviteOnly with no allowlist admits nobody new");
    }

    [Fact]
    public async Task Closed_refuses_new_logins_every_time()
    {
        var client = Client((MultiUserSettings.SignUpModeKey, "Closed"));
        var issuer = NewIssuer();

        (await Resolve(client, issuer, "stranger")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Resolve(client, issuer, "stranger")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the refusal must not have provisioned anything");
    }

    [Fact]
    public async Task InviteOnly_with_an_allowlist_lets_the_web_app_provision_invited_logins()
    {
        var client = Client((MultiUserSettings.SignUpModeKey, "InviteOnly"), ("Auth:Oidc:AllowedEmails:0", "invited@family.test"));

        var resp = await Resolve(client, NewIssuer(), "invited", "invited@family.test");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_web_app_can_refuse_provisioning_even_when_the_api_would_allow_it()
    {
        var client = Client((MultiUserSettings.SignUpLegacyOpenKey, "true"));

        var resp = await Resolve(client, NewIssuer(), "not-on-allowlist", allowProvisioning: false);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LegacyOpen_keeps_the_old_open_sign_up()
    {
        var client = Client((MultiUserSettings.SignUpLegacyOpenKey, "true"));

        var resp = await Resolve(client, NewIssuer(), "anyone");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Existing_logins_keep_signing_in_after_sign_up_is_closed()
    {
        var issuer = NewIssuer();
        var first = await (await Resolve(Client((MultiUserSettings.SignUpLegacyOpenKey, "true")), issuer, "member")).Content.ReadFromJsonAsync<JsonElement>();

        var again = await Resolve(Client((MultiUserSettings.SignUpModeKey, "Closed")), issuer, "member");

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("tenantId").GetGuid().Should().Be(first.GetProperty("tenantId").GetGuid());
        body.GetProperty("created").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_configured_household_owner_can_still_bootstrap_while_sign_up_is_closed()
    {
        var issuer = NewIssuer();
        var client = Client((MultiUserSettings.SignUpModeKey, "Closed"), ("Tenancy:LegacyOwnerSubject", "the-owner"), ("Tenancy:LegacyOwnerIssuer", issuer));

        var resp = await Resolve(client, issuer, "the-owner");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isHousehold").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Without_oidc_on_the_api_nothing_is_gated()
    {
        // The default host (no Auth:Oidc) keeps the pre-#145 behaviour: the API does not enforce a sign-up policy it was not told about.
        var client = factory.CreateAuthenticatedClient();

        var resp = await Resolve(client, NewIssuer(), "anyone");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Open_signup_blocks_quiltt_profile_creation_for_a_non_household_tenant()
    {
        var client = Client(
            (MultiUserSettings.SignUpLegacyOpenKey, "true"),
            ("Quiltt:Enabled", "true"), ("Quiltt:Environment", "sandbox"), ("Quiltt:ApiKeySecret", "quiltt-test-secret"), ("Quiltt:ConnectorId", "connector-test"));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        var resp = await client.PostAsync("/api/integrations/quiltt/session", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(SignUpMode.Open, false, true)]
    [InlineData(SignUpMode.Open, true, false)]
    [InlineData(SignUpMode.InviteOnly, false, false)]
    [InlineData(SignUpMode.Closed, false, false)]
    public void Billable_gate_applies_only_to_open_sign_up_without_the_explicit_allowance(SignUpMode mode, bool allowOpen, bool blocks) =>
        new SignUpPolicy(true, mode, false, allowOpen).BlocksUninvitedBillableProvisioning.Should().Be(blocks);

    [Fact]
    public void Without_oidc_the_policy_gates_nothing() =>
        new SignUpPolicy(false, SignUpMode.Closed, false).Should().Match<SignUpPolicy>(p => p.AllowsNewTenants && !p.BlocksUninvitedBillableProvisioning);
}
