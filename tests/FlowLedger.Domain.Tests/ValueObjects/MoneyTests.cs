using FlowLedger.Domain.Exceptions;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.ValueObjects;

public sealed class MoneyTests
{
    private static readonly Currency Usd = Currency.Usd;
    private static readonly Currency Eur = Currency.Eur;

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Money_constructs_with_valid_amount_and_currency()
    {
        var m = new Money(100.50m, Usd);
        m.Amount.Should().Be(100.50m);
        m.Currency.Should().Be(Usd);
    }

    [Fact]
    public void Money_allows_negative_amount_for_balance_representation()
    {
        var m = new Money(-50m, Usd);
        m.IsNegative.Should().BeTrue();
    }

    [Fact]
    public void Money_zero_factory_returns_zero_amount()
    {
        var m = Money.Zero(Usd);
        m.IsZero.Should().BeTrue();
        m.Currency.Should().Be(Usd);
    }

    // ── Arithmetic ────────────────────────────────────────────────────────────

    [Fact]
    public void Add_same_currency_returns_correct_sum()
    {
        var a = new Money(30m, Usd);
        var b = new Money(20m, Usd);
        (a + b).Amount.Should().Be(50m);
    }

    [Fact]
    public void Subtract_same_currency_returns_correct_difference()
    {
        var a = new Money(100m, Usd);
        var b = new Money(35m, Usd);
        (a - b).Amount.Should().Be(65m);
    }

    [Fact]
    public void Multiply_scales_amount_correctly()
    {
        var m = new Money(10m, Usd);
        (m * 3m).Amount.Should().Be(30m);
        (3m * m).Amount.Should().Be(30m);
    }

    [Fact]
    public void Negate_flips_sign()
    {
        var m = new Money(50m, Usd);
        (-m).Amount.Should().Be(-50m);
    }

    [Fact]
    public void Add_currency_mismatch_throws_CurrencyMismatchException()
    {
        var usd = new Money(10m, Usd);
        var eur = new Money(10m, Eur);
        var act = () => _ = usd + eur;
        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Subtract_currency_mismatch_throws_CurrencyMismatchException()
    {
        var usd = new Money(10m, Usd);
        var eur = new Money(5m, Eur);
        var act = () => _ = usd - eur;
        act.Should().Throw<CurrencyMismatchException>();
    }

    // ── GuardPositive ─────────────────────────────────────────────────────────

    [Fact]
    public void GuardPositive_passes_for_positive_amount()
    {
        var m = new Money(1m, Usd);
        var act = () => m.GuardPositive("test");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GuardPositive_throws_for_non_positive_amount(decimal amount)
    {
        var m = new Money(amount, Usd);
        var act = () => m.GuardPositive("test");
        act.Should().Throw<NegativeOrZeroAmountException>();
    }

    // ── Comparison ────────────────────────────────────────────────────────────

    [Fact]
    public void Greater_than_comparison_works()
    {
        (new Money(10m, Usd) > new Money(5m, Usd)).Should().BeTrue();
        (new Money(5m, Usd) > new Money(10m, Usd)).Should().BeFalse();
    }

    [Fact]
    public void Comparison_currency_mismatch_throws()
    {
        var act = () => _ = new Money(10m, Usd) > new Money(10m, Eur);
        act.Should().Throw<CurrencyMismatchException>();
    }

    // ── Equality ──────────────────────────────────────────────────────────────

    [Fact]
    public void Value_equality_holds_for_same_amount_and_currency()
    {
        var a = new Money(42m, Usd);
        var b = new Money(42m, Usd);
        a.Should().Be(b);
    }

    [Fact]
    public void Abs_returns_positive_value()
    {
        new Money(-25m, Usd).Abs().Amount.Should().Be(25m);
        new Money(25m, Usd).Abs().Amount.Should().Be(25m);
    }

    // ── Decimal precision ─────────────────────────────────────────────────────

    [Fact]
    public void Arithmetic_preserves_decimal_precision_without_floating_point_error()
    {
        // Classic floating-point trap: 0.1 + 0.2 != 0.3 in IEEE 754.
        var a = new Money(0.1m, Usd);
        var b = new Money(0.2m, Usd);
        (a + b).Amount.Should().Be(0.3m);
    }
}
