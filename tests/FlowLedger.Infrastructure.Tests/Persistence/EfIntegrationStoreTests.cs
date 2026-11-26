using FlowLedger.Application.Features.Integrations;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#154: integrations and integration_deliveries moved from ad-hoc Npgsql tables to EF (migration + tenant filter + guard).</summary>
[Collection("Integration")]
public sealed class EfIntegrationStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static readonly string Key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray());

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private EfIntegrationStore Store(TestTenantContext tenant, string? key = null)
        => new(fixture.CreateProductionStyleContext(tenant), tenant, key);

    private static readonly DateTimeOffset Fixed = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static Integration NewIntegration(string name) => new(
        Guid.NewGuid(), name, IntegrationKind.Webhook, true,
        new IntegrationConfig("https://example.com/hook", IntegrationAuth.None, null, new Dictionary<string, string>(), ["bill_due"], null),
        false, false, Fixed, null, null);

    [DockerFact]
    public async Task Integrations_round_trip_are_tenant_scoped_and_secrets_are_sealed()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var integ = NewIntegration("Zeta hook");
        var store = Store(a, Key);

        await store.SaveAsync(a.TenantId, integ, "cred-1", false, "sign-1");
        await store.SaveAsync(a.TenantId, NewIntegration("alpha hook"), null, false, null);

        var list = await store.ListAsync(a.TenantId);
        list.Select(i => i.Name).Should().Equal("alpha hook", "Zeta hook");
        var read = await store.GetAsync(a.TenantId, integ.Id);
        read!.HasCredential.Should().BeTrue();
        read.HasSigningSecret.Should().BeTrue();
        read.Config.Events.Should().Equal("bill_due");
        (await store.GetSecretsAsync(a.TenantId, integ.Id)).Should().Be(new IntegrationSecrets("cred-1", "sign-1"));

        // Saving again without secrets keeps them; clearing the credential keeps the signing secret.
        await store.SaveAsync(a.TenantId, integ, null, false, null);
        (await store.GetSecretsAsync(a.TenantId, integ.Id)).Should().Be(new IntegrationSecrets("cred-1", "sign-1"));
        await store.SaveAsync(a.TenantId, integ, null, true, null);
        (await store.GetSecretsAsync(a.TenantId, integ.Id)).Should().Be(new IntegrationSecrets(null, "sign-1"));

        await using (var conn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT secrets FROM integrations WHERE tenant_id = @t AND id = @i", conn);
            cmd.Parameters.AddWithValue("t", a.TenantId);
            cmd.Parameters.AddWithValue("i", integ.Id);
            ((string)(await cmd.ExecuteScalarAsync())!).Should().StartWith("v1:").And.NotContain("sign-1");
        }

        var other = Store(b, Key);
        (await other.ListAsync(b.TenantId)).Should().BeEmpty();
        (await other.GetAsync(b.TenantId, integ.Id)).Should().BeNull();
        (await other.DeleteAsync(b.TenantId, integ.Id)).Should().BeFalse("another tenant cannot delete it");
        (await store.GetAsync(a.TenantId, integ.Id)).Should().NotBeNull();
    }

    [DockerFact]
    public async Task Secrets_are_refused_without_a_key_and_another_tenant_is_refused_loudly()
    {
        var a = TestTenantContext.New();
        var store = Store(a);
        store.CanStoreSecrets.Should().BeFalse();

        var act = () => store.SaveAsync(a.TenantId, NewIntegration("x"), "secret", false, null);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var cross = () => store.ListAsync(Guid.NewGuid());
        await cross.Should().ThrowAsync<InvalidOperationException>();
    }

    [DockerFact]
    public async Task Deliveries_are_logged_trimmed_listed_and_deleted_with_the_integration()
    {
        var a = TestTenantContext.New();
        var store = Store(a, Key);
        var integ = NewIntegration("hook");
        await store.SaveAsync(a.TenantId, integ, null, false, null);

        var start = Fixed;
        for (var i = 0; i < 105; i++)
        {
            await store.RecordDeliveryAsync(a.TenantId, new IntegrationDelivery(
                Guid.NewGuid(), integ.Id, start.AddMinutes(i), "test", "POST", "example.com", i == 104 ? null : 200, 12, i == 104 ? "boom" : null));
        }

        var log = await store.ListDeliveriesAsync(a.TenantId, integ.Id, 500);
        log.Should().HaveCount(100, "the log keeps the most recent 100");
        log[0].Error.Should().Be("boom");
        (await store.ListDeliveriesAsync(a.TenantId, integ.Id, 5)).Should().HaveCount(5);
        (await store.GetAsync(a.TenantId, integ.Id))!.LastError.Should().Be("boom");

        (await store.DeleteAsync(a.TenantId, integ.Id)).Should().BeTrue();
        (await store.ListDeliveriesAsync(a.TenantId, integ.Id, 50)).Should().BeEmpty();
    }
}
