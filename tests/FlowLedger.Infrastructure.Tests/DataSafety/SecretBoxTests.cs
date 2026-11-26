using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence.Repositories;
using FlowLedger.Infrastructure.Tests.Helpers;
using FluentAssertions;
using Npgsql;

namespace FlowLedger.Infrastructure.Tests.DataSafety;

/// <summary>#156: v2 envelope (key id + AAD binding), v1 compatibility, and the rotation path.</summary>
[Collection("Integration")]
public sealed class SecretBoxTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private static byte[] NewKey(int seed) => new byte[32].Select((_, i) => (byte)(i + seed)).ToArray();
    private static string B64(byte[] k) => Convert.ToBase64String(k);

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    public async Task InitializeAsync() => await fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void V2_round_trips_and_carries_the_key_id()
    {
        var key = NewKey(1);
        var ring = new SecretKeyRing(key);
        var aad = SecretBox.Aad(TenantA, "p");
        var sealedValue = SecretBox.SealV2("hello", ring, aad);

        sealedValue.Should().StartWith($"v2:{SecretBox.KeyId(key)}:").And.NotContain("hello");
        SecretBox.Open(sealedValue, ring, aad).Should().Be("hello");
    }

    [Fact]
    public void A_value_copied_to_another_tenant_purpose_or_row_does_not_open()
    {
        var ring = new SecretKeyRing(NewKey(1));
        var row = Guid.NewGuid();
        var sealedValue = SecretBox.SealV2("secret", ring, SecretBox.Aad(TenantA, "p", row));

        SecretBox.Open(sealedValue, ring, SecretBox.Aad(TenantB, "p", row)).Should().BeNull("other tenant");
        SecretBox.Open(sealedValue, ring, SecretBox.Aad(TenantA, "q", row)).Should().BeNull("other purpose");
        SecretBox.Open(sealedValue, ring, SecretBox.Aad(TenantA, "p", Guid.NewGuid())).Should().BeNull("other row");
        SecretBox.Open(sealedValue, ring, SecretBox.Aad(TenantA, "p", row)).Should().Be("secret");
    }

    [Fact]
    public void V1_values_still_open_with_any_ring_key_and_no_aad()
    {
        var oldKey = NewKey(1);
        var v1 = SecretBox.Seal("legacy", oldKey);

        SecretBox.Open(v1, new SecretKeyRing(NewKey(9), [oldKey]), SecretBox.Aad(TenantA, "p")).Should().Be("legacy");
        SecretBox.Open(v1, new SecretKeyRing(NewKey(9)), SecretBox.Aad(TenantA, "p")).Should().BeNull();
    }

    [Fact]
    public void Rotation_v1_under_old_key_opens_with_ring_then_reseals_as_v2_under_new_key_alone()
    {
        var oldKey = NewKey(1);
        var newKey = NewKey(50);
        var aad = SecretBox.Aad(TenantA, "p");
        var v1 = SecretBox.Seal("rotate me", oldKey);
        var ring = new SecretKeyRing(newKey, [oldKey]);

        SecretBox.NeedsReseal(v1, ring).Should().BeTrue();
        var v2 = SecretBox.Reseal(v1, ring, aad)!;
        v2.Should().StartWith($"v2:{SecretBox.KeyId(newKey)}:");
        SecretBox.NeedsReseal(v2, ring).Should().BeFalse();
        SecretBox.Open(v2, new SecretKeyRing(newKey), aad).Should().Be("rotate me");
    }

    [Fact]
    public void A_v2_value_sealed_under_a_retired_key_id_does_not_open_and_a_bad_previous_key_is_ignored()
    {
        var retired = new SecretKeyRing(NewKey(1));
        var v2 = SecretBox.SealV2("x", retired, SecretBox.Aad(TenantA, "p"));
        SecretBox.Open(v2, new SecretKeyRing(NewKey(2)), SecretBox.Aad(TenantA, "p")).Should().BeNull();

        var ring = SecretKeyRing.From(B64(NewKey(2)), "not-base64!!", B64(new byte[5]), B64(NewKey(1)));
        ring.Fallbacks.Should().HaveCount(1);
        SecretBox.Open(v2, ring, SecretBox.Aad(TenantA, "p")).Should().Be("x");
    }

    [DockerFact]
    public async Task Reencryptor_moves_v1_rows_to_v2_under_the_new_key_and_is_idempotent()
    {
        var oldKey = NewKey(1);
        var newKey = NewKey(50);
        var tenant = TestTenantContext.New();
        const string url = "https://ha.example.com/api/webhook/AbCdEf0123456789XyZ";

        // A row written by the pre-v2 code path (v1 under the old key).
        var ctx = fixture.CreateProductionStyleContext(tenant);
        await new EfHomeAssistantStore(ctx, tenant, B64(oldKey)).SaveAsync(tenant.TenantId, true, url, false, false, true, 3, HaEventKinds.All);
        await using (var conn = new NpgsqlConnection(fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE home_assistant_settings SET webhook = @w WHERE tenant_id = @t", conn);
            cmd.Parameters.AddWithValue("w", SecretBox.Seal(url, oldKey));
            cmd.Parameters.AddWithValue("t", tenant.TenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        var ring = new SecretKeyRing(newKey, [oldKey]);
        var store = new EfHomeAssistantStore(fixture.CreateProductionStyleContext(tenant), tenant, ring: ring);
        (await store.GetWebhookUrlAsync(tenant.TenantId)).Should().Be(url, "the previous key still opens v1 rows");

        var run = new SecretReencryptor(fixture.ConnectionString, ring);
        (await run.RunAsync()).Resealed.Should().Be(1);
        (await run.RunAsync()).Should().Be(new ReencryptResult(0, 1, 0));

        var newOnly = new EfHomeAssistantStore(fixture.CreateProductionStyleContext(tenant), tenant, ring: new SecretKeyRing(newKey));
        (await newOnly.GetWebhookUrlAsync(tenant.TenantId)).Should().Be(url, "after re-encryption the old key can be retired");
    }
}
