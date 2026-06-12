using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

public sealed class CurrencyTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("eur")]   // case-insensitive
    [InlineData(" GBP ")] // whitespace trimmed
    public void Currency_valid_codes_create_successfully(string code)
    {
        var currency = new Currency(code);
        currency.Code.Should().Be(code.Trim().ToUpperInvariant());
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDO")]
    [InlineData("123")]
    [InlineData("U$D")]
    public void Currency_invalid_codes_throw_ArgumentException(string code)
    {
        var act = () => new Currency(code);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Currency_null_or_whitespace_throws_EmptyStringException()
    {
        var act = () => new Currency("   ");
        act.Should().Throw<EmptyStringException>();
    }

    [Fact]
    public void Currency_value_equality_works()
    {
        var a = new Currency("USD");
        var b = new Currency("usd");
        a.Should().Be(b);
    }

    [Fact]
    public void Currency_inequality_across_different_codes()
    {
        Currency.Usd.Should().NotBe(Currency.Eur);
    }
}
