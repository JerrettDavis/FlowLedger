using System.Globalization;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

/// <summary>Fingerprints and Money text must not depend on the process culture (#129 C-17).</summary>
public sealed class CultureInvariantFormattingTests
{
    private static readonly AccountId Account = AccountId.From(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    private static T UnderCulture<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Fingerprint_is_byte_identical_under_de_DE_and_en_US()
    {
        string Make() => TransactionFingerprint.FromRawDescription(Account, new DateOnly(2026, 1, 2), 12.5m, "Coffee").Value;

        var de = UnderCulture("de-DE", Make);
        var us = UnderCulture("en-US", Make);

        de.Should().Be(us);
        de.Should().Contain("|12.5000|");
    }

    [Fact]
    public void Money_ToString_is_invariant_under_de_DE()
        => UnderCulture("de-DE", () => new Money(1234.5m, new Currency("USD")).ToString()).Should().Be("1234.50 USD");
}
