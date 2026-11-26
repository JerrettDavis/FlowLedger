using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

/// <summary>#154: Home Assistant settings and the sent ledger moved from ad-hoc Npgsql tables to EF.</summary>
[Collection("Integration")]
public sealed class HomeAssistantStoreTests : IAsyncLifetime
{
    private const string Url = "https://ha.example.com/api/webhook/AbCdEf0123456789XyZ";
    private static readonly string Key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
    private readonly IntegrationTestFixture _fixture;

    public HomeAssistantStoreTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private EfHomeAssistantStore Store(TestTenantContext tenant, string? key = null)
        => new(_fixture.CreateProductionStyleContext(tenant), tenant, key);

    [DockerFact]
    public async Task Defaults_are_off_and_alerts_only()
    {
        var t = TestTenantContext.New();
        var s = await Store(t).GetAsync(t.TenantId);
        s.Enabled.Should().BeFalse();
        s.ShareAmounts.Should().BeFalse();
        s.HasWebhook.Should().BeFalse();
    }

    [DockerFact]
    public async Task A_webhook_is_refused_when_no_encryption_key_is_configured()
    {
        var t = TestTenantContext.New();
        var store = Store(t);
        store.CanStoreWebhook.Should().BeFalse();

        var act = () => store.SaveAsync(t.TenantId, true, Url, false, false, true, 3, HaEventKinds.All);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Settings without a URL are fine.
        await store.SaveAsync(t.TenantId, false, null, false, false, true, 3, HaEventKinds.All);
    }

    [DockerFact]
    public async Task Webhook_is_encrypted_at_rest_and_never_in_the_read_model()
    {
        var t = TestTenantContext.New();
        var store = Store(t, Key);
        await store.SaveAsync(t.TenantId, true, Url, false, false, true, 3, HaEventKinds.All);

        var read = await store.GetAsync(t.TenantId);
        read.HasWebhook.Should().BeTrue();
        read.WebhookHost.Should().Be("ha.example.com");
        System.Text.Json.JsonSerializer.Serialize(read).Should().NotContain("AbCdEf0123456789XyZ");

        (await store.GetWebhookUrlAsync(t.TenantId)).Should().Be(Url);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT webhook FROM home_assistant_settings WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", t.TenantId);
        var raw = (string)(await cmd.ExecuteScalarAsync())!;
        raw.Should().StartWith("v1:").And.NotContain("AbCdEf0123456789XyZ").And.NotContain("ha.example.com");

        // A store without the key cannot open it (fails closed instead of leaking or throwing).
        (await Store(t).GetWebhookUrlAsync(t.TenantId)).Should().BeNull();
    }

    [DockerFact]
    public async Task Saving_without_a_url_keeps_the_stored_one_and_clear_removes_it()
    {
        var t = TestTenantContext.New();
        var store = Store(t, Key);
        await store.SaveAsync(t.TenantId, true, Url, false, false, true, 3, HaEventKinds.All);

        await store.SaveAsync(t.TenantId, true, null, false, true, false, 5, [HaEventKinds.Overdraft]);
        (await store.GetWebhookUrlAsync(t.TenantId)).Should().Be(Url);
        var s = await store.GetAsync(t.TenantId);
        (s.ShareAmounts, s.IncludeNames, s.LeadDays).Should().Be((true, false, 5));
        s.Kinds.Should().Equal(HaEventKinds.Overdraft);

        await store.SaveAsync(t.TenantId, false, null, true, false, true, 3, HaEventKinds.All);
        (await store.GetWebhookUrlAsync(t.TenantId)).Should().BeNull();
        (await store.GetAsync(t.TenantId)).HasWebhook.Should().BeFalse();
    }

    [DockerFact]
    public async Task Dedupe_claims_are_atomic_per_tenant_and_releasable()
    {
        var a = TestTenantContext.New();
        var b = TestTenantContext.New();
        var storeA = Store(a);
        var storeB = Store(b);

        (await storeA.TryClaimAsync(a.TenantId, "bill_due_soon:x:2026-11-20")).Should().BeTrue();
        (await storeA.TryClaimAsync(a.TenantId, "bill_due_soon:x:2026-11-20")).Should().BeFalse();
        (await storeB.TryClaimAsync(b.TenantId, "bill_due_soon:x:2026-11-20")).Should().BeTrue();

        // Tenant B's release and prune of the same key must not touch tenant A's claim.
        await storeB.ReleaseAsync(b.TenantId, "bill_due_soon:x:2026-11-20");
        await storeB.PruneAsync(b.TenantId, DateTimeOffset.UtcNow.AddDays(1));
        (await storeA.TryClaimAsync(a.TenantId, "bill_due_soon:x:2026-11-20")).Should().BeFalse();

        await storeA.ReleaseAsync(a.TenantId, "bill_due_soon:x:2026-11-20");
        (await storeA.TryClaimAsync(a.TenantId, "bill_due_soon:x:2026-11-20")).Should().BeTrue();

        await storeA.PruneAsync(a.TenantId, DateTimeOffset.UtcNow.AddDays(1));
        (await storeA.TryClaimAsync(a.TenantId, "bill_due_soon:x:2026-11-20")).Should().BeTrue();
    }

    [DockerFact]
    public async Task Delivery_result_is_recorded()
    {
        var t = TestTenantContext.New();
        var store = Store(t, Key);
        await store.SaveAsync(t.TenantId, true, Url, false, false, true, 3, HaEventKinds.All);

        await store.RecordResultAsync(t.TenantId, "Could not reach Home Assistant.");
        (await store.GetAsync(t.TenantId)).LastError.Should().Be("Could not reach Home Assistant.");

        await store.RecordResultAsync(t.TenantId, null);
        var s = await store.GetAsync(t.TenantId);
        s.LastError.Should().BeNull();
        s.LastSentUtc.Should().NotBeNull();
    }

    [DockerFact]
    public async Task Another_tenants_settings_are_refused_loudly()
    {
        var t = TestTenantContext.New();
        var act = () => Store(t).GetAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
