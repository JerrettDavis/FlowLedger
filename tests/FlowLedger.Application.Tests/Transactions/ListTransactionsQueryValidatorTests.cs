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

    [Theory]
    [InlineData("date", "asc")]
    [InlineData("MERCHANT", "desc")]
    [InlineData("description", null)]
    [InlineData("amount", "ASC")]
    [InlineData(null, null)]
    public void Accepts_the_known_sort_keys(string? sort, string? dir) =>
        _validator.Validate(new ListTransactionsQuery(Sort: sort, Dir: dir)).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("balance", null)]
    [InlineData("date; drop table", null)]
    [InlineData("date", "sideways")]
    public void Rejects_unknown_sort_keys_and_directions(string sort, string? dir) =>
        _validator.Validate(new ListTransactionsQuery(Sort: sort, Dir: dir)).IsValid.Should().BeFalse();

    [Fact]
    public void Sort_defaults_to_newest_first()
    {
        var query = new ListTransactionsQuery();

        query.SortKey.Should().Be(FlowLedger.Application.Abstractions.TransactionSortKey.Date);
        query.Descending.Should().BeTrue();
        new ListTransactionsQuery(Sort: "amount", Dir: "asc").SortKey.Should().Be(FlowLedger.Application.Abstractions.TransactionSortKey.Amount);
        new ListTransactionsQuery(Sort: "amount", Dir: "asc").Descending.Should().BeFalse();
    }
}
