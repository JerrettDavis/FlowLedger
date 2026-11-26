using FlowLedger.Integrations.Quiltt;
using FlowLedger.Integrations.SimpleFin;
using FluentAssertions;

namespace FlowLedger.Integrations.Tests.Quiltt;

/// <summary>#128 C-22 / C-23: undated and projected provider rows must not become ledger entries.</summary>
public sealed class IngestionFilterTests
{
    [Theory]
    [InlineData("2026-01-02", "SETTLED", true)]
    [InlineData("2026-01-02", "PENDING", true)]
    [InlineData("2026-01-02", "PROJECTED", false)]
    [InlineData(null, "SETTLED", false)]
    [InlineData("not-a-date", "SETTLED", false)]
    public void Quiltt_IsIngestible(string? date, string status, bool expected)
        => QuilttMapper.IsIngestible(new QTransaction { Date = date, Status = status }).Should().Be(expected);

    [Theory]
    [InlineData(1_700_000_000L, null, true)]
    [InlineData(0L, 1_700_000_000L, true)]
    [InlineData(0L, null, false)]
    public void SimpleFin_HasUsableDate(long posted, long? transactedAt, bool expected)
        => SimpleFinMapper.HasUsableDate(new SfTransaction { Posted = posted, TransactedAt = transactedAt }).Should().Be(expected);
}
