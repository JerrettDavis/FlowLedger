using FlowLedger.Application.Features.Alerts;
using FlowLedger.Application.Features.HomeAssistant;
using FlowLedger.Application.Features.Integrations;
using FlowLedger.Application.Tests.Fakes;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Alerts;

/// <summary>#70: alerts also reach webhooks subscribed to <c>alert.raised</c>, once each, through the existing outbound dispatch.</summary>
public sealed class AlertDeliveryServiceTests
{
    private sealed class FakeIntegrationStore : IIntegrationStore
    {
        public bool CanStoreSecrets => true;
        public Dictionary<Guid, Integration> Items { get; } = [];

        public Task<IReadOnlyList<Integration>> ListAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Integration>>([.. Items.Values]);

        public Task<Integration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));

        public Task<IntegrationSecrets> GetSecretsAsync(Guid tenantId, Guid id, CancellationToken ct = default) => Task.FromResult(new IntegrationSecrets(null, null));

        public Task SaveAsync(Guid tenantId, Integration integration, string? credential, bool clearCredential, string? signingSecret, CancellationToken ct = default)
        {
            Items[integration.Id] = integration;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default) => Task.FromResult(Items.Remove(id));

        public Task RecordDeliveryAsync(Guid tenantId, IntegrationDelivery delivery, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<IntegrationDelivery>> ListDeliveriesAsync(Guid tenantId, Guid id, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IntegrationDelivery>>([]);
    }

    private sealed class FakeTransport : IIntegrationTransport
    {
        public List<TransportRequest> Sent { get; } = [];
        public int Status { get; set; } = 200;

        public Task<TransportResponse> SendAsync(TransportRequest request, bool allowLocalNetwork, CancellationToken ct = default)
        {
            Sent.Add(request);
            return Task.FromResult(new TransportResponse(Status, "application/json", "{}", false, null));
        }
    }

    private sealed class Network : IHomeAssistantNetworkPolicy
    {
        public bool AllowLocalNetwork(Guid tenantId) => false;
    }

    private sealed class FakeHomeAssistantStore(HomeAssistantSettings settings) : IHomeAssistantStore
    {
        public HashSet<string> Claims { get; } = [];
        public bool CanStoreWebhook => true;

        public Task<HomeAssistantSettings> GetAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(settings);

        public Task<string?> GetWebhookUrlAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task SaveAsync(Guid tenantId, bool enabled, string? webhookUrl, bool clearWebhook, bool shareAmounts, bool includeNames, int leadDays, IReadOnlyList<string> kinds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> TryClaimAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default) => Task.FromResult(Claims.Add(dedupeKey));

        public Task ReleaseAsync(Guid tenantId, string dedupeKey, CancellationToken ct = default)
        {
            Claims.Remove(dedupeKey);
            return Task.CompletedTask;
        }

        public Task RecordResultAsync(Guid tenantId, string? error, CancellationToken ct = default) => Task.CompletedTask;

        public Task PruneAsync(Guid tenantId, DateTimeOffset olderThan, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAlerts(params HaEvent[] events) : IAlertEventSource
    {
        public DateOnly Today => new(2026, 11, 20);

        public Task<IReadOnlyList<HaEvent>> DetectAsync(DateOnly today, HomeAssistantSettings settings, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HaEvent>>(events);
    }

    private static readonly HaEvent PriceUp = new(HaEventKinds.PriceIncrease, "warning", "Price increase", "Streamly Video went up.", "price_increase:abc", "Streamly Video", new DateOnly(2026, 11, 19));
    private static readonly HaEvent Large = new(HaEventKinds.LargeCharge, "warning", "Large charge", "A large charge landed.", "large_charge:def");

    private static async Task<(AlertDeliveryService Delivery, FakeTransport Transport, FakeHomeAssistantStore Store)> CreateAsync(
        string[]? webhookEvents, params HaEvent[] alerts)
    {
        var store = new FakeIntegrationStore();
        var transport = new FakeTransport();
        var tenant = new FakeTenantContext();
        var integrations = new IntegrationService(store, transport, new Network(), tenant, FlowLedger.TestSupport.TestClock.Provider);
        if (webhookEvents is not null)
        {
            await integrations.SaveAsync(null, new IntegrationSaveRequest(
                "Hook", IntegrationKind.Webhook, true, "https://hooks.example.com/in", IntegrationAuth.None, null, null, false, null, webhookEvents, null));
        }

        var claims = new FakeHomeAssistantStore(HomeAssistantSettings.Default);
        return (new AlertDeliveryService(new FakeAlerts(alerts), claims, integrations, tenant), transport, claims);
    }

    [Fact]
    public async Task A_new_alert_is_posted_to_a_subscribed_webhook_exactly_once()
    {
        var (delivery, transport, _) = await CreateAsync([IntegrationEvents.AlertRaised], PriceUp);

        var first = await delivery.RunAsync();
        var second = await delivery.RunAsync();

        first.Should().Be(new AlertDeliveryResult(1, 1, 0, 0));
        second.Should().Be(new AlertDeliveryResult(1, 0, 1, 0), "each condition fires once");
        var request = transport.Sent.Should().ContainSingle().Subject;
        request.Body.Should().Contain("\"event\":\"alert.raised\"").And.Contain("\"kind\":\"price_increase\"").And.Contain("Price increase");
        request.Headers.Should().Contain(h => h.Key == IntegrationService.EventHeader && h.Value == "alert.raised");
    }

    [Fact]
    public async Task The_same_alert_carries_the_same_delivery_id_so_a_receiver_can_drop_a_replay()
    {
        var (delivery, transport, store) = await CreateAsync([IntegrationEvents.AlertRaised], PriceUp);
        await delivery.RunAsync();
        store.Claims.Clear(); // as if the claim were lost, and the alert re-sent
        await delivery.RunAsync();

        transport.Sent.Select(r => r.Headers.Single(h => h.Key == IntegrationService.DeliveryHeader).Value).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Nothing_is_detected_or_sent_when_no_webhook_listens_for_alerts()
    {
        var (withoutAnyWebhook, transport1, _) = await CreateAsync(null, PriceUp);
        var (listeningToSyncOnly, transport2, _) = await CreateAsync([IntegrationEvents.SyncCompleted], PriceUp);

        (await withoutAnyWebhook.RunAsync()).Should().Be(new AlertDeliveryResult(0, 0, 0, 0));
        (await listeningToSyncOnly.RunAsync()).Should().Be(new AlertDeliveryResult(0, 0, 0, 0));
        transport1.Sent.Should().BeEmpty();
        transport2.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_delivery_the_receiver_rejects_is_retried_on_the_next_run()
    {
        var (delivery, transport, _) = await CreateAsync([IntegrationEvents.AlertRaised], PriceUp, Large);
        transport.Status = 500;

        var failing = await delivery.RunAsync();
        transport.Status = 200;
        var recovered = await delivery.RunAsync();

        failing.Should().Be(new AlertDeliveryResult(2, 0, 0, 2));
        recovered.Should().Be(new AlertDeliveryResult(2, 2, 0, 0), "the claims were released, so both alerts go out now");
    }
}
