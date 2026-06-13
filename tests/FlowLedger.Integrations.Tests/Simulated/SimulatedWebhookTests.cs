using System.Text;
using System.Text.Json;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Simulated;

/// <summary>
/// Tests for webhook signature verification and payload parsing
/// in <see cref="SimulatedFinancialDataProvider"/>.
/// </summary>
public sealed class SimulatedWebhookTests
{
    private static SimulatedFinancialDataProvider MakeProvider() =>
        new(Options.Create(new SimulatedProviderOptions()));

    // ── VerifyWebhookAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyWebhook_accepts_valid_test_signature()
    {
        var provider = MakeProvider();
        var (body, signature) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent();

        // Must not throw
        await provider.VerifyWebhookAsync(body, signature);
    }

    [Fact]
    public async Task VerifyWebhook_rejects_tampered_signature()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent();
        const string badSignature = "000000000000000000000000000000000000000000000000000000000000dead";

        Func<Task> act = () => provider.VerifyWebhookAsync(body, badSignature);
        await act.Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    [Fact]
    public async Task VerifyWebhook_rejects_empty_signature()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent();

        Func<Task> act = () => provider.VerifyWebhookAsync(body, string.Empty);
        await act.Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    [Fact]
    public async Task VerifyWebhook_rejects_signature_for_different_body()
    {
        var provider = MakeProvider();
        var (_, signature) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent();

        // Different payload — same signature should be invalid
        var differentBody = Encoding.UTF8.GetBytes("{\"event_type\":\"TAMPERED\"}");

        Func<Task> act = () => provider.VerifyWebhookAsync(differentBody, signature);
        await act.Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    // ── ParseWebhookAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ParseWebhook_returns_correct_event_type()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent("SYNC_COMPLETE");

        var evt = await provider.ParseWebhookAsync(body);

        evt.EventType.Should().Be("SYNC_COMPLETE");
    }

    [Fact]
    public async Task ParseWebhook_returns_correct_member_id()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(
            memberId: "sim-member-custom");

        var evt = await provider.ParseWebhookAsync(body);

        evt.MemberId.Should().Be("sim-member-custom");
    }

    [Fact]
    public async Task ParseWebhook_occurred_at_is_recent()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent();

        var evt = await provider.ParseWebhookAsync(body);

        evt.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, precision: TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task BuildSyntheticWebhookEvent_produces_verifiable_signature()
    {
        var provider = MakeProvider();

        for (var i = 0; i < 5; i++)
        {
            var (body, sig) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent($"EVENT_{i}");

            // Should not throw
            await provider.VerifyWebhookAsync(body, sig);
        }
    }

    // ── BuildTestSignature helper ─────────────────────────────────────────────

    [Fact]
    public void BuildTestSignature_is_deterministic_for_same_input()
    {
        var body = Encoding.UTF8.GetBytes("{\"test\":\"payload\"}");

        var sig1 = SimulatedFinancialDataProvider.BuildTestSignature(body);
        var sig2 = SimulatedFinancialDataProvider.BuildTestSignature(body);

        sig1.Should().Be(sig2);
    }

    [Fact]
    public void BuildTestSignature_differs_for_different_input()
    {
        var body1 = Encoding.UTF8.GetBytes("{\"event\":\"A\"}");
        var body2 = Encoding.UTF8.GetBytes("{\"event\":\"B\"}");

        var sig1 = SimulatedFinancialDataProvider.BuildTestSignature(body1);
        var sig2 = SimulatedFinancialDataProvider.BuildTestSignature(body2);

        sig1.Should().NotBe(sig2);
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseWebhook_malformed_json_throws_FatalProviderException_not_JsonException()
    {
        var provider = MakeProvider();
        var badJson = Encoding.UTF8.GetBytes("this is { not valid ] JSON at all");

        Func<Task> act = () => provider.ParseWebhookAsync(badJson);

        // Must surface as FatalProviderException — NOT a raw JsonException.
        var ex = await act.Should().ThrowAsync<FatalProviderException>();
        ex.WithInnerException<JsonException>(
            "inner JsonException must be preserved for diagnostics");
    }

    [Fact]
    public async Task ParseWebhook_invalid_occurred_at_date_does_not_throw_and_falls_back_to_UtcNow()
    {
        var provider = MakeProvider();
        var payload = new
        {
            event_type = "SYNC_COMPLETE",
            member_id = "sim-member-001",
            occurred_at = "not-a-valid-date",
            metadata = new { source = "test" },
        };
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);

        var evt = await provider.ParseWebhookAsync(body);

        // Must not throw, must return a valid timestamp close to now
        evt.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, precision: TimeSpan.FromSeconds(5));
        evt.EventType.Should().Be("SYNC_COMPLETE");
    }
}
