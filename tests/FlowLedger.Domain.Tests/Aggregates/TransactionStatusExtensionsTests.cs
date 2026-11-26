using FlowLedger.Domain.Aggregates;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

/// <summary>Pins the set of "real" (ledger-affecting) statuses used by every Application-layer filter (#106).</summary>
public sealed class TransactionStatusExtensionsTests
{
    [Fact]
    public void Only_planned_and_skipped_rows_are_not_real()
    {
        var real = Enum.GetValues<TransactionStatus>().Where(s => s.IsReal()).ToList();

        real.Should().BeEquivalentTo(new[]
        {
            TransactionStatus.Pending, TransactionStatus.Posted, TransactionStatus.Matched,
            TransactionStatus.Reconciled, TransactionStatus.Ignored, TransactionStatus.NeedsReview,
        });
    }

    [Theory]
    [InlineData("Planned", false)]
    [InlineData("Skipped", false)]
    [InlineData("Posted", true)]
    [InlineData("Pending", true)]
    [InlineData("Reconciled", true)]
    [InlineData("SomethingNew", true)]
    public void String_form_matches_the_enum_form_and_treats_unknown_values_as_real(string status, bool expected)
        => TransactionStatusExtensions.IsRealStatus(status).Should().Be(expected);
}
