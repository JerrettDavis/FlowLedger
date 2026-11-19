using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.Persistence;

[Collection("Integration")]
public sealed class HomeAssistantStoreTests : IAsyncLifetime
{
    private const string Url = "https://ha.example.com/api/webhook/AbCdEf0123456789XyZ";
    private static readonly string Key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
    private readonly IntegrationTestFixture _fixture;

    public HomeAssistantStoreTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task Defaults_are_off_and_alerts_only()
    {
        var s = await new NpgsqlHomeAssistantStore(_fixture.ConnectionString).GetAsync(Guid.NewGuid());
        s.Enabled.Should().BeFalse();
        s.ShareAmounts.Should().BeFalse();
        s.HasWebhook.Should().BeFalse();
    }

    [DockerFact]
    public async Task A_webhook_is_refused_when_no_encryption_key_is_configured()
    {
        var store = new NpgsqlHomeAssistantStore(_fixture.ConnectionString);
        store.CanStoreWebhook.Should().BeFalse();

        var act = () => store.SaveAsync(Guid.NewGuid(), true, Url, false, false, true, 3, HaEventKinds.All);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Settings without a URL are fine.
        await store.SaveAsync(Guid.NewGuid(), false, null, false, false, true, 3, HaEventKinds.All);
    }

    [DockerFact]
    public async Task Webhook_is_encrypted_at_rest_and_never_in_the_read_model()
    {
        var store = new NpgsqlHomeAssistantStore(_fixture.ConnectionString, Key);
        var tenant = Guid.NewGuid();
        await store.SaveAsync(tenant, true, Url, false, false, true, 3, HaEventKinds.All);

        var read = await store.GetAsync(tenant);
        read.HasWebhook.Should().BeTrue();
        read.WebhookHost.Should().Be("ha.example.com");
        System.Text.Json.JsonSerializer.Serialize(read).Should().NotContain("AbCdEf0123456789XyZ");

        (await store.GetWebhookUrlAsync(tenant)).Should().Be(Url);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT webhook FROM home_assistant_settings WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        var raw = (string)(await cmd.ExecuteScalarAsync())!;
        raw.Should().StartWith("v1:").And.NotContain("AbCdEf0123456789XyZ").And.NotContain("ha.example.com");

        // A store without the key cannot open it (fails closed instead of leaking or throwing).
        (await new NpgsqlHomeAssistantStore(_fixture.ConnectionString).GetWebhookUrlAsync(tenant)).Should().BeNull();
    }

    [DockerFact]
    public async Task Saving_without_a_url_keeps_the_stored_one_and_clear_removes_it()
    {
        var store = new NpgsqlHomeAssistantStore(_fixture.ConnectionString, Key);
        var tenant = Guid.NewGuid();
        await store.SaveAsync(tenant, true, Url, false, false, true, 3, HaEventKinds.All);

        await store.SaveAsync(tenant, true, null, false, true, false, 5, [HaEventKinds.Overdraft]);
        (await store.GetWebhookUrlAsync(tenant)).Should().Be(Url);
        var s = await store.GetAsync(tenant);
        (s.ShareAmounts, s.IncludeNames, s.LeadDays).Should().Be((true, false, 5));
        s.Kinds.Should().Equal(HaEventKinds.Overdraft);

        await store.SaveAsync(tenant, false, null, true, false, true, 3, HaEventKinds.All);
        (await store.GetWebhookUrlAsync(tenant)).Should().BeNull();
        (await store.GetAsync(tenant)).HasWebhook.Should().BeFalse();
    }

    [DockerFact]
    public async Task Dedupe_claims_are_atomic_per_tenant_and_releasable()
    {
        var store = new NpgsqlHomeAssistantStore(_fixture.ConnectionString);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        (await store.TryClaimAsync(a, "bill_due_soon:x:2026-11-20")).Should().BeTrue();
        (await store.TryClaimAsync(a, "bill_due_soon:x:2026-11-20")).Should().BeFalse();
        (await store.TryClaimAsync(b, "bill_due_soon:x:2026-11-20")).Should().BeTrue();

        await store.ReleaseAsync(a, "bill_due_soon:x:2026-11-20");
        (await store.TryClaimAsync(a, "bill_due_soon:x:2026-11-20")).Should().BeTrue();

        await store.PruneAsync(a, DateTimeOffset.UtcNow.AddDays(1));
        (await store.TryClaimAsync(a, "bill_due_soon:x:2026-11-20")).Should().BeTrue();
    }

    [DockerFact]
    public async Task Delivery_result_is_recorded()
    {
        var store = new NpgsqlHomeAssistantStore(_fixture.ConnectionString, Key);
        var tenant = Guid.NewGuid();
        await store.SaveAsync(tenant, true, Url, false, false, true, 3, HaEventKinds.All);

        await store.RecordResultAsync(tenant, "Could not reach Home Assistant.");
        (await store.GetAsync(tenant)).LastError.Should().Be("Could not reach Home Assistant.");

        await store.RecordResultAsync(tenant, null);
        var s = await store.GetAsync(tenant);
        s.LastError.Should().BeNull();
        s.LastSentUtc.Should().NotBeNull();
    }
}
