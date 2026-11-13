using System.Text;
using FlowLedger.Integrations.Abstractions;
using FluentAssertions;

namespace FlowLedger.Integrations.Tests.Mx;

/// <summary>
/// Retention guarantee: every MX response body is archived byte-for-byte BEFORE it is parsed,
/// and a failed archive write fails the call rather than silently dropping paid-for data.
/// </summary>
public sealed class MxRawArchiveTests : IClassFixture<MxWireMockFixture>
{
    private readonly MxWireMockFixture _fixture;

    public MxRawArchiveTests(MxWireMockFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_response_is_archived_verbatim_before_parsing()
    {
        var archive = new InMemoryRawPayloadArchive();
        var client = _fixture.CreateApiClient(archive);

        await client.GetAccountsAsync(MxWireMockFixture.UserGuid, MxWireMockFixture.MemberGuid, 1, 25, CancellationToken.None);
        await client.GetTransactionsAsync(MxWireMockFixture.UserGuid, MxWireMockFixture.CheckingAccountGuid, 1, 25, CancellationToken.None);

        archive.Items.Select(i => i.Operation).Should().Equal("GetAccounts", "GetTransactions");
        archive.Items.Should().OnlyContain(i => i.Provider == "MX");
        archive.Items.Last().Context.Should().NotBeNull();
        archive.Items.Last().Context!.PathAndQuery.Should().Contain(MxWireMockFixture.CheckingAccountGuid).And.Contain("page=1");
        archive.Items.Last().Context!.StatusCode.Should().Be(200);
        Encoding.UTF8.GetString(archive.Items.Last().Payload).Should().Contain(MxWireMockFixture.CheckingAccountGuid);
    }

    [Fact]
    public async Task Failed_archive_write_fails_the_call()
    {
        var archive = new InMemoryRawPayloadArchive { FailOnArchive = true };
        var client = _fixture.CreateApiClient(archive);

        var act = () => client.GetAccountsAsync(MxWireMockFixture.UserGuid, MxWireMockFixture.MemberGuid, 1, 25, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>("data must never be consumed without being retained");
    }
}
