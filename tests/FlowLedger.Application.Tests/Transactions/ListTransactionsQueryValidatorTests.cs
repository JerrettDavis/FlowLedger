using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Transactions;

/// <summary>The Transactions page requests its full window (5000 rows); the API must accept exactly that and no more.</summary>
public sealed class ListTransactionsQueryValidatorTests
{
    private readonly ListTransactionsQueryValidator _validator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(5000)]
    public void Accepts_take_up_to_5000(int take) =>
        _validator.Validate(new ListTransactionsQuery(Take: take)).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public void Rejects_take_outside_range(int take) =>
        _validator.Validate(new ListTransactionsQuery(Take: take)).IsValid.Should().BeFalse();
}
