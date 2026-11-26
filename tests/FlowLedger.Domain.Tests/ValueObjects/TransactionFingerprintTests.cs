using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

/// <summary>
/// Golden values: fingerprints are persisted and used for dedupe, so normalisation output must never drift.
/// These expectations were verified against the original private NormalizeDescription copies in
/// ImportTransactionsHandler and FinancialSyncService before they were consolidated here.
/// </summary>
public sealed class TransactionFingerprintTests
{
    public static TheoryData<string, string> Golden => new()
    {
        { "", "UNKNOWN" },
        { "   ", "UNKNOWN" },
        { "\t\r\n", "UNKNOWN" },
        { "Coffee Shop", "COFFEE SHOP" },
        { "  Coffee\t\tShop \n #12 ", "COFFEE SHOP #12" },
        { "café bar", "CAFÉ BAR" },
        { " em  space ", "EM SPACE" },
        { "zero​width", "ZERO​WIDTH" },
        { "istanbul", "ISTANBUL" },
        { "straße", "STRAßE" },
        { "AMZN Mktp US*2K4", "AMZN MKTP US*2K4" },
    };

    private static readonly AccountId Account = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Theory]
    [MemberData(nameof(Golden))]
    public void NormalizeDescription_is_stable(string raw, string expected)
        => TransactionFingerprint.NormalizeDescription(raw).Should().Be(expected);

    [Fact]
    public void FromRawDescription_without_provider_id_matches_golden_value()
        => TransactionFingerprint.FromRawDescription(Account, new DateOnly(2026, 1, 2), 12.5m, "  Coffee\t\tShop ").Value
            .Should().Be("11111111-2222-3333-4444-555555555555|2026-01-02|12.5000|COFFEE SHOP");

    [Fact]
    public void FromRawDescription_with_provider_id_matches_golden_value()
        => TransactionFingerprint.FromRawDescription(Account, new DateOnly(2026, 1, 2), 12.5m, "", "tx-9").Value
            .Should().Be("pid:tx-9|11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Occurrence_zero_keeps_legacy_value_and_later_occurrences_are_suffixed()
    {
        var date = new DateOnly(2026, 1, 2);
        var legacy = TransactionFingerprint.FromRawDescription(Account, date, 2.75m, "Transit").Value;

        TransactionFingerprint.FromRawDescription(Account, date, 2.75m, "Transit", occurrence: 0).Value.Should().Be(legacy);
        TransactionFingerprint.FromRawDescription(Account, date, 2.75m, "Transit", occurrence: 1).Value.Should().Be(legacy + "|1");
        TransactionFingerprint.FromRawDescription(Account, date, 2.75m, "Transit", "tx-1", occurrence: 3).Value
            .Should().Be("pid:tx-1|11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public void FromRawDescription_equals_Create_with_normalized_description()
        => TransactionFingerprint.FromRawDescription(Account, new DateOnly(2026, 1, 2), 3m, " a  b ")
            .Should().Be(TransactionFingerprint.Create(Account, new DateOnly(2026, 1, 2), 3m, "A B"));
}
