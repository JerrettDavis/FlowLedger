using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Mx.Mapping;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>#128 C-19 / C-22: missing pagination metadata must not move the watermark; undated rows are not stored.</summary>
public sealed class MxIngestionEdgeCaseTests : IClassFixture<MxWireMockFixture>
{
    private const int PerPage = 2;

    private readonly MxWireMockFixture _fixture;

    public MxIngestionEdgeCaseTests(MxWireMockFixture fixture) => _fixture = fixture;

    private static string Tx(string account, string guid, string? date) =>
        "{\"guid\":\"" + guid + "\",\"account_guid\":\"" + account + "\",\"amount\":5.00,"
        + "\"currency_code\":\"USD\"," + (date is null ? "" : "\"date\":\"" + date + "\",")
        + "\"description\":\"Shop\",\"status\":\"POSTED\",\"type\":\"DEBIT\"}";

    [Fact]
    public async Task Response_without_pagination_metadata_and_a_full_page_keeps_paging_instead_of_writing_the_watermark()
    {
        const string acct = "ACT-nopag-full";
        var account = $"{MxWireMockFixture.UserGuid}|{acct}";
        _fixture.StubTransactionsBody(
            acct,
            "{\"transactions\":[" + Tx(acct, "T1", "2026-01-02") + "," + Tx(acct, "T2", "2026-01-03") + "]}");
        var provider = _fixture.CreateProvider(recordsPerPage: PerPage);

        var page = await provider.GetTransactionsAsync(account, SyncCursor.Initial, pageSize: PerPage);

        page.Items.Should().HaveCount(2);
        page.HasMore.Should().BeTrue("a full page with no pagination metadata may be truncated");
        MxCursor.Decode(page.NextCursor, PerPage).Page.Should().Be(2, "the cursor resumes on the next page, not a date watermark");
    }

    [Fact]
    public async Task Response_without_pagination_metadata_and_a_short_page_completes_normally()
    {
        const string acct = "ACT-nopag-short";
        var account = $"{MxWireMockFixture.UserGuid}|{acct}";
        _fixture.StubTransactionsBody(
            acct, "{\"transactions\":[" + Tx(acct, "T1", "2026-01-02") + "]}");
        var provider = _fixture.CreateProvider(recordsPerPage: PerPage);

        var page = await provider.GetTransactionsAsync(account, SyncCursor.Initial, pageSize: PerPage);

        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Transaction_with_no_date_is_skipped_and_counted_not_stored_as_year_0001()
    {
        const string acct = "ACT-nodate";
        var account = $"{MxWireMockFixture.UserGuid}|{acct}";
        _fixture.StubTransactionsBody(
            acct,
            "{\"transactions\":[" + Tx(acct, "T1", "2026-01-02") + "," + Tx(acct, "T-NODATE", null) + "],"
            + "\"pagination\":{\"current_page\":1,\"per_page\":2,\"total_entries\":2,\"total_pages\":1}}");
        var provider = _fixture.CreateProvider(recordsPerPage: PerPage);

        var page = await provider.GetTransactionsAsync(account, SyncCursor.Initial, pageSize: PerPage);

        page.Items.Should().ContainSingle().Which.ProviderId.Should().Be("T1");
        page.Items.Should().NotContain(t => t.PostedDate == DateOnly.MinValue);
        page.SkippedCount.Should().Be(1);
    }
}
