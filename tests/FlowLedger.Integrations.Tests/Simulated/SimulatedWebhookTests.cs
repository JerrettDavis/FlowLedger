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
    private const string TestSecret = "unit-test-webhook-secret";

    private static SimulatedFinancialDataProvider MakeProvider() =>
        new(Options.Create(new SimulatedProviderOptions { WebhookSecret = TestSecret }), FlowLedger.TestSupport.TestClock.Provider);

    // ── VerifyWebhookAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyWebhook_accepts_valid_test_signature()
    {
        var provider = MakeProvider();
        var (body, signature) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret);

        // Must not throw
        await provider.VerifyWebhookAsync(body, signature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task VerifyWebhook_fails_closed_when_no_secret_is_configured(string? secret)
    {
        var provider = new SimulatedFinancialDataProvider(
            Options.Create(new SimulatedProviderOptions { WebhookSecret = secret }));
        var (body, signature) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent("anything");

        Func<Task> act = () => provider.VerifyWebhookAsync(body, signature);
        await act.Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    [Fact]
    public async Task VerifyWebhook_rejects_tampered_signature()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret);
        const string badSignature = "000000000000000000000000000000000000000000000000000000000000dead";

        Func<Task> act = () => provider.VerifyWebhookAsync(body, badSignature);
        await act.Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    [Fact]
    public async Task VerifyWebhook_rejects_empty_signature()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret);

        Func<Task> act = () => provider.VerifyWebhookAsync(body, string.Empty);
        await act.Should().ThrowAsync<InvalidWebhookSignatureException>();
    }

    [Fact]
    public async Task VerifyWebhook_rejects_signature_for_different_body()
    {
        var provider = MakeProvider();
        var (_, signature) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret);

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
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret, "SYNC_COMPLETE");

        var evt = await provider.ParseWebhookAsync(body);

        evt.EventType.Should().Be("SYNC_COMPLETE");
    }

    [Fact]
    public async Task ParseWebhook_returns_correct_member_id()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret,
            memberId: "sim-member-custom");

        var evt = await provider.ParseWebhookAsync(body);

        evt.MemberId.Should().Be("sim-member-custom");
    }

    [Fact]
    public async Task ParseWebhook_occurred_at_is_recent()
    {
        var provider = MakeProvider();
        var (body, _) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret, time: FlowLedger.TestSupport.TestClock.Provider);

        var evt = await provider.ParseWebhookAsync(body);

        evt.OccurredAt.Should().Be(FlowLedger.TestSupport.TestClock.Instant);
    }

    [Fact]
    public async Task BuildSyntheticWebhookEvent_produces_verifiable_signature()
    {
        var provider = MakeProvider();

        for (var i = 0; i < 5; i++)
        {
            var (body, sig) = SimulatedFinancialDataProvider.BuildSyntheticWebhookEvent(TestSecret, $"EVENT_{i}");

            // Should not throw
            await provider.VerifyWebhookAsync(body, sig);
        }
    }

    // ── BuildTestSignature helper ─────────────────────────────────────────────

    [Fact]
    public void BuildSignature_is_deterministic_for_same_input()
    {
        var body = Encoding.UTF8.GetBytes("{\"test\":\"payload\"}");

        var sig1 = SimulatedFinancialDataProvider.BuildSignature(body, TestSecret);
        var sig2 = SimulatedFinancialDataProvider.BuildSignature(body, TestSecret);

        sig1.Should().Be(sig2);
    }

    [Fact]
    public void BuildSignature_differs_for_different_input()
    {
        var body1 = Encoding.UTF8.GetBytes("{\"event\":\"A\"}");
        var body2 = Encoding.UTF8.GetBytes("{\"event\":\"B\"}");

        var sig1 = SimulatedFinancialDataProvider.BuildSignature(body1, TestSecret);
        var sig2 = SimulatedFinancialDataProvider.BuildSignature(body2, TestSecret);

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
        evt.OccurredAt.Should().Be(FlowLedger.TestSupport.TestClock.Instant);
        evt.EventType.Should().Be("SYNC_COMPLETE");
    }
}
