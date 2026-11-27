using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Users;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>
/// HTTP contract of the linked-logins endpoints (<c>/api/users/*</c>) and the opt-in email auto-link / owner adoption
/// rules. Uses unique issuers per test because the user directory tables are created lazily and so are not cleared
/// by the between-test database reset.
/// </summary>
[Collection("ApiIntegration")]
public sealed class UserLinkingEndpointTests(FlowLedgerApiFactory factory, AutoLinkApiFactory autoLinkFactory) : IAsyncLifetime
{
    private sealed record Resolved(Guid UserId, Guid TenantId, bool Created);

    private sealed record CodeResponse(string Code, DateTimeOffset ExpiresUtc);

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewIssuer() => "https://idp.test/" + Guid.NewGuid();

    private static async Task<Resolved> Resolve(FlowLedgerApiFactory f, string issuer, string subject, string? email = null, bool verified = false)
    {
        var resp = await f.CreateAuthenticatedClient().PostAsJsonAsync("/api/users/resolve",
            new { issuer, subject, email, displayName = subject, emailVerified = verified });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<Resolved>())!;
    }

    private Task<Resolved> Resolve(string issuer, string subject, string? email = null, bool verified = false)
        => Resolve(factory, issuer, subject, email, verified);

    private static async Task<string> IssueCode(HttpClient owner)
    {
        var resp = await owner.PostAsync("/api/users/link-code", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<CodeResponse>())!.Code;
    }

    private static Task<HttpResponseMessage> Redeem(HttpClient client, Guid userId, string code)
        => client.PostAsJsonAsync("/api/users/link", new { userId, code });

    [Fact]
    public async Task Link_code_is_valid_for_about_fifteen_minutes()
    {
        var alice = await Resolve(NewIssuer(), "alice");
        var resp = await factory.CreateAuthenticatedClient(alice.TenantId).PostAsync("/api/users/link-code", null);
        var code = (await resp.Content.ReadFromJsonAsync<CodeResponse>())!;

        code.ExpiresUtc.Should().BeCloseTo(factory.ServerNow.AddMinutes(15), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Link_code_is_single_use()
    {
        var issuer = NewIssuer();
        var alice = await Resolve(issuer, "alice");
        var bob = await Resolve(issuer, "bob");
        var carol = await Resolve(issuer, "carol");
        var code = await IssueCode(factory.CreateAuthenticatedClient(alice.TenantId));

        var first = await Redeem(factory.CreateAuthenticatedClient(bob.TenantId), bob.UserId, code);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await Redeem(factory.CreateAuthenticatedClient(carol.TenantId), carol.UserId, code);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Resolve(issuer, "carol")).TenantId.Should().Be(carol.TenantId, "a spent code must not move anyone");
    }

    [Fact]
    public async Task Wrong_link_code_is_rejected()
    {
        var issuer = NewIssuer();
        var bob = await Resolve(issuer, "bob");

        var resp = await Redeem(factory.CreateAuthenticatedClient(bob.TenantId), bob.UserId, "WRONG-CODE");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Expired_link_code_is_rejected()
    {
        var issuer = NewIssuer();
        var alice = await Resolve(issuer, "alice");
        var bob = await Resolve(issuer, "bob");
        var code = await IssueCode(factory.CreateAuthenticatedClient(alice.TenantId));

        await using (var conn = new NpgsqlConnection(factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE app_user_link_codes SET expires_utc = now() - interval '1 minute' WHERE tenant_id = @t", conn);
            cmd.Parameters.AddWithValue("t", alice.TenantId);
            (await cmd.ExecuteNonQueryAsync()).Should().BeGreaterThan(0);
        }

        var resp = await Redeem(factory.CreateAuthenticatedClient(bob.TenantId), bob.UserId, code);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Resolve(issuer, "bob")).TenantId.Should().Be(bob.TenantId);
    }

    [Fact]
    public async Task Redeeming_for_a_login_that_belongs_to_another_tenant_is_forbidden()
    {
        var issuer = NewIssuer();
        var alice = await Resolve(issuer, "alice");
        var bob = await Resolve(issuer, "bob");
        var mallory = await Resolve(issuer, "mallory");
        var code = await IssueCode(factory.CreateAuthenticatedClient(alice.TenantId));

        // Mallory (tenant header = her own tenant) tries to move Bob's login using Alice's code.
        var resp = await Redeem(factory.CreateAuthenticatedClient(mallory.TenantId), bob.UserId, code);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Resolve(issuer, "bob")).TenantId.Should().Be(bob.TenantId);

        // The refused attempt must not have burned the code.
        (await Redeem(factory.CreateAuthenticatedClient(bob.TenantId), bob.UserId, code)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Linked_login_sees_the_same_tenant_data_and_is_listed()
    {
        var issuer = NewIssuer();
        var alice = await Resolve(issuer, "alice");
        var aliceClient = factory.CreateAuthenticatedClient(alice.TenantId);
        var created = await aliceClient.PostAsJsonAsync("/api/accounts",
            CreateAccountRequestBuilder.A().Named("Joint Checking").OfType("Checking").StartingAt(10m).Build());
        var account = (await created.Content.ReadFromJsonAsync<AccountDto>())!;

        var other = await Resolve(issuer, "alice-second-login");
        other.TenantId.Should().NotBe(alice.TenantId);
        var otherClient = factory.CreateAuthenticatedClient(other.TenantId);
        (await otherClient.GetFromJsonAsync<List<AccountDto>>("/api/accounts"))!.Should().BeEmpty();

        (await Redeem(otherClient, other.UserId, await IssueCode(aliceClient))).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await Resolve(issuer, "alice-second-login");
        after.TenantId.Should().Be(alice.TenantId);
        var linkedClient = factory.CreateAuthenticatedClient(after.TenantId);
        (await linkedClient.GetFromJsonAsync<List<AccountDto>>("/api/accounts"))!.Should().Contain(a => a.Id == account.Id);

        var logins = await aliceClient.GetFromJsonAsync<List<LinkedIdentity>>("/api/users/identities");
        logins!.Select(l => l.UserId).Should().BeEquivalentTo([alice.UserId, other.UserId]);
    }

    [Fact]
    public async Task The_last_login_of_a_tenant_cannot_be_unlinked_but_an_extra_one_can()
    {
        var issuer = NewIssuer();
        var alice = await Resolve(issuer, "alice");
        var aliceClient = factory.CreateAuthenticatedClient(alice.TenantId);

        (await aliceClient.DeleteAsync($"/api/users/identities/{alice.UserId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await aliceClient.DeleteAsync($"/api/users/identities/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var bob = await Resolve(issuer, "bob");
        await Redeem(factory.CreateAuthenticatedClient(bob.TenantId), bob.UserId, await IssueCode(aliceClient));

        (await aliceClient.DeleteAsync($"/api/users/identities/{bob.UserId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await aliceClient.GetFromJsonAsync<List<LinkedIdentity>>("/api/users/identities"))!.Should().ContainSingle(l => l.UserId == alice.UserId);
        (await Resolve(issuer, "bob")).TenantId.Should().NotBe(alice.TenantId, "the detached login gets its own tenant back");
    }

    [Fact]
    public async Task Resolve_requires_issuer_and_subject()
    {
        var client = factory.CreateAuthenticatedClient();

        (await client.PostAsJsonAsync("/api/users/resolve", new { issuer = "", subject = "x", email = "a@b.test", emailVerified = true }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/users/resolve", new { issuer = NewIssuer(), subject = " " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Email_auto_link_is_disabled_by_default_even_for_verified_matching_emails()
    {
        var email = $"same-{Guid.NewGuid():N}@home.test";
        var first = await Resolve(NewIssuer(), "one", email, verified: true);
        var second = await Resolve(NewIssuer(), "two", email, verified: true);

        second.TenantId.Should().NotBe(first.TenantId);
    }

    [Fact]
    public async Task Auto_link_joins_the_existing_tenant_only_for_a_verified_email()
    {
        var email = $"same-{Guid.NewGuid():N}@home.test";
        var first = await Resolve(autoLinkFactory, NewIssuer(), "one", email, verified: true);

        var verified = await Resolve(autoLinkFactory, NewIssuer(), "two", email.ToUpperInvariant(), verified: true);
        var unverified = await Resolve(autoLinkFactory, NewIssuer(), "three", email, verified: false);
        var otherEmail = await Resolve(autoLinkFactory, NewIssuer(), "four", $"other-{Guid.NewGuid():N}@home.test", verified: true);

        verified.TenantId.Should().Be(first.TenantId);
        unverified.TenantId.Should().NotBe(first.TenantId, "an unverified email could be claimed by anyone");
        otherEmail.TenantId.Should().NotBe(first.TenantId);
    }

    [Fact]
    public async Task Auto_link_does_not_trust_an_unverified_first_login_as_the_anchor()
    {
        var email = $"same-{Guid.NewGuid():N}@home.test";
        var unverifiedFirst = await Resolve(autoLinkFactory, NewIssuer(), "one", email, verified: false);

        var verifiedSecond = await Resolve(autoLinkFactory, NewIssuer(), "two", email, verified: true);

        verifiedSecond.TenantId.Should().NotBe(unverifiedFirst.TenantId);
    }

    [Fact]
    public async Task Owner_email_adopts_the_household_tenant_only_when_verified_and_from_the_configured_issuer()
    {
        var household = FlowLedgerApiFactory.DemoTenantId;
        var unverified = await Resolve(autoLinkFactory, AutoLinkApiFactory.OwnerIssuer, "o1", AutoLinkApiFactory.OwnerEmail, verified: false);
        var wrongIssuer = await Resolve(autoLinkFactory, NewIssuer(), "o2", AutoLinkApiFactory.OwnerEmail, verified: true);
        var owner = await Resolve(autoLinkFactory, AutoLinkApiFactory.OwnerIssuer, "o3", AutoLinkApiFactory.OwnerEmail, verified: true);

        unverified.TenantId.Should().NotBe(household);
        wrongIssuer.TenantId.Should().NotBe(household);
        owner.TenantId.Should().Be(household);
    }
}
