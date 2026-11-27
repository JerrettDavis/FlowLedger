using FlowLedger.Application.Features.Transactions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Tests.Transactions;

public class TransactionEnrichmentTests
{
    [Theory]
    [InlineData("SQ *BLUE BOTTLE COFFEE #1234 SAN FRANCISCO CA 94103", "Blue Bottle Coffee San Francisco")]
    [InlineData("TST* TORCHYS TACOS 0042 AUSTIN TX", "Torchys Tacos Austin")]
    [InlineData("Starbucks", null)]
    [InlineData("", null)]
    public void Suggests_readable_names(string raw, string? expected) =>
        Assert.Equal(expected, MerchantNameSuggester.Suggest(raw));

    [Fact]
    public void Tags_reflect_direction_amount_and_keywords()
    {
        var tags = AutoTagger.Suggest(null, "NETFLIX.COM 800-585", "Debit", 15m, TransactionStatus.Posted);
        Assert.Contains("subscription", tags);
        Assert.Contains("online", tags);
        Assert.DoesNotContain("large", tags);

        Assert.Contains("large", AutoTagger.Suggest(null, "MORTGAGE PMT", "Debit", 1800m, TransactionStatus.Posted));
        Assert.Contains("refund", AutoTagger.Suggest(null, "AMAZON REFUND", "Credit", 20m, TransactionStatus.Posted));
        Assert.DoesNotContain("refund", AutoTagger.Suggest(null, "AMAZON REFUND", "Debit", 20m, TransactionStatus.Posted));
        Assert.Contains("pending", AutoTagger.Suggest(null, "SHELL OIL", "Debit", 40m, TransactionStatus.Pending));
    }

    [Fact]
    public void ApplyAutoTags_replaces_only_vocabulary_tags_and_is_idempotent()
    {
        var tx = Transaction.RecordActual(TenantId.From(Guid.NewGuid()), AccountId.From(Guid.NewGuid()), new Money(20m, Currency.Usd), TransactionDirection.Debit,
            "NETFLIX.COM", new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5), TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);

        Assert.True(tx.ApplyAutoTags(["subscription", "online"], AutoTagger.Vocabulary));
        Assert.False(tx.ApplyAutoTags(["online", "subscription"], AutoTagger.Vocabulary));

        // A later run drops auto tags that no longer apply.
        Assert.True(tx.ApplyAutoTags(["online"], AutoTagger.Vocabulary));
        Assert.Equal(["online"], tx.Tags);
    }
}
