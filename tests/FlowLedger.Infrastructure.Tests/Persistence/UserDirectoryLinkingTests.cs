using FlowLedger.Application.Features.Users;
using FlowLedger.Infrastructure.DataSafety;
using FluentAssertions;
using Testcontainers.PostgreSql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>Multi-login-per-person tenancy rules against a real Postgres.</summary>
public sealed class UserDirectoryLinkingTests : IAsyncLifetime
{
    private static readonly Guid Legacy = new("00000000-0000-0000-0000-000000000001");
    private const string Iss = "https://auth.example/application/o/flowledger/";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _pg.StartAsync();
    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    private NpgsqlUserDirectory Dir(LegacyTenantClaim? c = null) =>
        new(_pg.GetConnectionString(), c ?? new LegacyTenantClaim(Legacy, null));

    private static ExternalIdentity Id(string sub, string? email = null, bool verified = true) =>
        new(Iss, sub, email, sub, verified);

    [Fact]
    public async Task EveryConfiguredOwnerLogin_JoinsLegacyTenant_EvenAfterTheFirstAdoptedIt()
    {
        var dir = Dir(new LegacyTenantClaim(Legacy, "me@a.com; me@b.com", "sub-1, sub-2"));
        var a = await dir.ResolveAsync(Id("sub-1"));
        var b = await dir.ResolveAsync(Id("sub-2"));
        var c = await dir.ResolveAsync(Id("other-sub", "me@b.com"));
        a.TenantId.Should().Be(Legacy);
        b.TenantId.Should().Be(Legacy);
        c.TenantId.Should().Be(Legacy);
        (await dir.ListIdentitiesAsync(Legacy)).Should().HaveCount(3);
    }

    [Fact]
    public async Task NoOwnerConfigured_NobodyAdoptsLegacy_AndStrangersGetSeparateTenants()
    {
        var dir = Dir();
        var a = await dir.ResolveAsync(Id("a", "a@x.com"));
        var b = await dir.ResolveAsync(Id("b", "b@x.com"));
        a.TenantId.Should().NotBe(Legacy);
        b.TenantId.Should().NotBe(a.TenantId);
    }

    [Fact]
    public async Task UnverifiedOwnerEmail_DoesNotAdoptLegacy()
    {
        var dir = Dir(new LegacyTenantClaim(Legacy, "me@a.com"));
        (await dir.ResolveAsync(Id("x", "me@a.com", verified: false))).TenantId.Should().NotBe(Legacy);
    }

    [Fact]
    public async Task VerifiedEmailMatch_LinksSecondLoginToSameTenant_ButSpouseStaysSeparate()
    {
        var dir = Dir();
        var first = await dir.ResolveAsync(Id("one", "Me@Home.com"));
        var second = await dir.ResolveAsync(new ExternalIdentity("https://google", "g-1", "me@home.com", "me", true));
        var wife = await dir.ResolveAsync(Id("wife", "wife@home.com"));
        second.TenantId.Should().Be(first.TenantId);
        wife.TenantId.Should().NotBe(first.TenantId);
    }

    [Fact]
    public async Task UnverifiedEmail_DoesNotAutoLink_UnlessTrusted()
    {
        var dir = Dir();
        var first = await dir.ResolveAsync(Id("one", "me@home.com", verified: false));
        var second = await dir.ResolveAsync(Id("two", "me@home.com", verified: false));
        second.TenantId.Should().NotBe(first.TenantId);

        var trusting = Dir(new LegacyTenantClaim(Legacy, null, TrustUnverifiedEmail: true));
        var t1 = await trusting.ResolveAsync(Id("t1", "t@home.com", verified: false));
        var t2 = await trusting.ResolveAsync(Id("t2", "t@home.com", verified: false));
        t2.TenantId.Should().Be(t1.TenantId);
    }

    [Fact]
    public async Task LinkCode_MovesLoginToIssuingTenant_SingleUse_AndKeepsOldTenant()
    {
        var dir = Dir();
        var main = await dir.ResolveAsync(Id("main"));
        var alt = await dir.ResolveAsync(Id("alt"));
        alt.TenantId.Should().NotBe(main.TenantId);

        var code = await dir.CreateLinkCodeAsync(main.TenantId);
        (await dir.RedeemLinkCodeAsync(alt.UserId, code.Code.ToLowerInvariant())).Should().Be(LinkOutcome.Linked);
        (await dir.ResolveAsync(Id("alt"))).TenantId.Should().Be(main.TenantId);
        (await dir.ListIdentitiesAsync(main.TenantId)).Should().HaveCount(2);

        // Single use + the abandoned tenant is not deleted from background-job fan-out.
        (await dir.RedeemLinkCodeAsync(alt.UserId, code.Code)).Should().Be(LinkOutcome.InvalidOrExpired);
        (await dir.RedeemLinkCodeAsync(alt.UserId, "WRONGCODE1")).Should().Be(LinkOutcome.InvalidOrExpired);
    }

    [Fact]
    public async Task Unlink_RefusesLastLogin_AndGivesDetachedLoginItsOwnTenant()
    {
        var dir = Dir();
        var main = await dir.ResolveAsync(Id("main"));
        (await dir.UnlinkAsync(main.TenantId, main.UserId)).Should().BeFalse();

        var alt = await dir.ResolveAsync(Id("alt"));
        var code = await dir.CreateLinkCodeAsync(main.TenantId);
        await dir.RedeemLinkCodeAsync(alt.UserId, code.Code);
        (await dir.UnlinkAsync(main.TenantId, alt.UserId)).Should().BeTrue();
        (await dir.ResolveAsync(Id("alt"))).TenantId.Should().NotBe(main.TenantId);
    }

    [Fact]
    public async Task ExistingRowsFromBeforeThisMigration_KeepTheirTenant()
    {
        // Simulate the pre-migration table shape (no email_verified column) with an existing user.
        await using (var conn = new Npgsql.NpgsqlConnection(_pg.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "CREATE TABLE app_users (id uuid PRIMARY KEY, issuer text NOT NULL, subject text NOT NULL, email text NULL, display_name text NULL, " +
                "tenant_id uuid NOT NULL, created_utc timestamptz NOT NULL DEFAULT now(), last_login_utc timestamptz NOT NULL DEFAULT now(), UNIQUE (issuer, subject));" +
                $"INSERT INTO app_users (id, issuer, subject, tenant_id) VALUES (gen_random_uuid(), '{Iss}', 'old', '{Legacy}');", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var dir = Dir();
        (await dir.ResolveAsync(Id("old"))).TenantId.Should().Be(Legacy);
    }
}
