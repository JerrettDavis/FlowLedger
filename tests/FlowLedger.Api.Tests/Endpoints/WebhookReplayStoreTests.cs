using System.Text;
using FlowLedger.Api.Endpoints;
using FlowLedger.Application.Abstractions;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

/// <summary>#152: the shared replay store is the durable second line behind the in-process guard.</summary>
[Trait("Invariant", "INV-10")]
public sealed class WebhookReplayStoreTests
{
    private static byte[] NewBody() => Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());

    private static string Hash(byte[] body) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));

    private sealed class FakeStore : IWebhookReplayStore
    {
        public HashSet<string> Seen { get; } = [];
        public bool Throw { get; set; }

        public Task<bool> TryRecordAsync(string provider, string bodyHashHex, TimeSpan window, CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("db down");
            }

            return Task.FromResult(Seen.Add(provider + "|" + bodyHashHex));
        }
    }

    [Fact]
    public async Task A_body_another_instance_already_accepted_is_a_replay_even_though_this_process_never_saw_it()
    {
        var store = new FakeStore();
        var body = NewBody();
        await store.TryRecordAsync("MX", Hash(body), TimeSpan.FromMinutes(10)); // accepted by "another instance" or before a restart

        (await WebhookReplayGuard.SeenRecentlyAsync(body, "MX", store, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task First_delivery_passes_and_the_second_is_rejected_and_providers_are_independent()
    {
        var store = new FakeStore();
        var body = NewBody();

        (await WebhookReplayGuard.SeenRecentlyAsync(body, "MX", store, CancellationToken.None)).Should().BeFalse();
        (await WebhookReplayGuard.SeenRecentlyAsync(body, "MX", store, CancellationToken.None)).Should().BeTrue();

        var other = NewBody();
        (await WebhookReplayGuard.SeenRecentlyAsync(other, "MX", store, CancellationToken.None)).Should().BeFalse();
        store.Seen.Should().Contain("MX|" + Hash(other));
    }

    [Fact]
    public async Task An_unavailable_store_fails_open_so_a_legitimate_event_is_not_lost_and_no_store_works_too()
    {
        var store = new FakeStore { Throw = true };

        (await WebhookReplayGuard.SeenRecentlyAsync(NewBody(), "MX", store, CancellationToken.None)).Should().BeFalse();
        (await WebhookReplayGuard.SeenRecentlyAsync(NewBody(), "MX", null, CancellationToken.None)).Should().BeFalse();
    }
}
