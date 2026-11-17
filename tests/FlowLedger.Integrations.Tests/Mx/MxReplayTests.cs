using FlowLedger.Integrations.Mx;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>Archived responses must be replayable through the real client with zero network calls.</summary>
public sealed class MxReplayTests : IClassFixture<MxWireMockFixture>
{
    private readonly MxWireMockFixture _fixture;

    public MxReplayTests(MxWireMockFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Archived_responses_replay_through_the_real_client_without_hitting_the_network()
    {
        // 1. Capture from WireMock into on-disk archive files (same layout the production archive writes).
        var root = Path.Combine(Path.GetTempPath(), "fl-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var capture = new InMemoryRawPayloadArchive();
            var live = _fixture.CreateApiClient(capture);
            var liveAccounts = await live.GetAccountsAsync(MxWireMockFixture.UserGuid, MxWireMockFixture.MemberGuid, 1, 25, CancellationToken.None);

            var i = 0;
            foreach (var item in capture.Items)
            {
                var stem = Path.Combine(root, $"{++i:D19}-{item.Operation}-x");
                await File.WriteAllBytesAsync(stem + ".json", item.Payload);
                await File.WriteAllTextAsync(stem + ".meta.json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    method = item.Context!.Method,
                    pathAndQuery = item.Context.PathAndQuery,
                    statusCode = item.Context.StatusCode,
                }));
            }

            // 2. Replay with a handler that has no network: same request must yield the same parsed data.
            var handler = new MxArchiveReplayHandler(root);
            handler.Count.Should().Be(1);
            var replayClient = new MxApiClient(
                new HttpClient(handler) { BaseAddress = new Uri("http://never.invalid") },
                NullLogger<MxApiClient>.Instance, new InMemoryRawPayloadArchive());

            var replayed = await replayClient.GetAccountsAsync(MxWireMockFixture.UserGuid, MxWireMockFixture.MemberGuid, 1, 25, CancellationToken.None);

            replayed.Accounts!.Select(a => a.Guid).Should().Equal(liveAccounts.Accounts!.Select(a => a.Guid));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
