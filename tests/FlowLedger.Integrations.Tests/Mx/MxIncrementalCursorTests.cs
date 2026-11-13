using FlowLedger.Integrations.Abstractions;
using FluentAssertions;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Regression guard for silent data loss: MX lists newest-first, so a cursor parked at
/// "last page + 1" would never see transactions posted after a completed pass. After a full pass the
/// persisted cursor must restart at page 1 with a from_date watermark.
/// </summary>
public sealed class MxIncrementalCursorTests : IClassFixture<MxWireMockFixture>
{
    private readonly MxWireMockFixture _fixture;

    public MxIncrementalCursorTests(MxWireMockFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Completed_pass_leaves_a_page1_date_watermark_cursor_and_next_sync_requests_it()
    {
        var provider = _fixture.CreateProvider();
        var account = $"{MxWireMockFixture.UserGuid}|{MxWireMockFixture.CheckingAccountGuid}";

        var cursor = SyncCursor.Initial;
        TransactionPage page;
        var pages = 0;
        do
        {
            page = await provider.GetTransactionsAsync(account, cursor, pageSize: MxWireMockFixture.RecordsPerPage);
            cursor = page.NextCursor;
            pages++;
        }
        while (page.HasMore);

        pages.Should().Be(2, "30 transactions at 25/page");

        _fixture.Reset();
        await provider.GetTransactionsAsync(account, cursor, pageSize: MxWireMockFixture.RecordsPerPage);

        var request = _fixture.RequestLog.Last().RequestMessage!;
        request.Query!["page"].Should().ContainSingle().Which.Should().Be("1", "the next sync restarts at the newest page");
        request.Query["from_date"].Should().ContainSingle().Which.Should()
            .MatchRegex(@"^\d{4}-\d{2}-\d{2}$", "the watermark window bounds the re-read");
    }
}
